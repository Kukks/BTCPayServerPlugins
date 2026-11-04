using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.SecSwitch.Controllers;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchControllerTests
{
    readonly FakeSettingsRepository _settings = new();
    readonly LedgerStore _ledger;
    readonly LockdownGate _gate;
    readonly SecSwitchController _controller;

    public SecSwitchControllerTests()
    {
        _ledger = new LedgerStore(_settings);
        _gate = new LockdownGate(_settings, _ledger, NullLogger<LockdownGate>.Instance);
        _controller = new SecSwitchController(_settings, _ledger, _gate)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };
    }

    sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    Task Lock() => _ledger.RecordAsync(
        new LedgerEntry { AdvisoryId = "core-1", Status = LedgerStatus.Acted, LockedDown = true, RecordedAt = DateTimeOffset.UtcNow });

    [Fact]
    public async Task Settings_get_shows_the_defaults_until_something_is_saved()
    {
        var model = Assert.IsType<SecSwitchSettings>(Assert.IsType<ViewResult>(await _controller.Settings()).Model);

        Assert.False(model.Enabled);
        Assert.Equal(SecSwitchSettings.DefaultFeedUrl, model.FeedUrl);
        Assert.True(model.AutoApply);
        Assert.True(model.PreferUpdateOverDisable);
        Assert.False(model.AutoUpdateAll);
    }

    [Fact]
    public void The_default_feed_url_is_a_usable_https_url()
    {
        Assert.True(AdvisoryFeed.IsSupportedFeedUrl(SecSwitchSettings.DefaultFeedUrl));
    }

    [Fact]
    public async Task Saving_valid_settings_persists_them_and_redirects()
    {
        var posted = new SecSwitchSettings { Enabled = true, FeedUrl = "https://example.test/a.json", AutoUpdateAll = true };

        var result = await _controller.Settings(posted);

        Assert.Equal(nameof(SecSwitchController.Settings), Assert.IsType<RedirectToActionResult>(result).ActionName);
        var saved = (await _settings.GetSettingAsync<SecSwitchSettings>())!;
        Assert.True(saved.Enabled);
        Assert.True(saved.AutoUpdateAll);
        Assert.Equal("https://example.test/a.json", saved.FeedUrl);
        Assert.NotNull(_controller.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Theory]
    [InlineData("http://example.test/a.json")]
    [InlineData("/relative")]
    [InlineData("")]
    public async Task Saving_a_non_https_feed_url_is_rejected_and_nothing_is_persisted(string url)
    {
        var posted = new SecSwitchSettings { Enabled = true, FeedUrl = url };

        var result = await _controller.Settings(posted);

        Assert.Same(posted, Assert.IsType<ViewResult>(result).Model);
        Assert.False(_controller.ModelState.IsValid);
        Assert.Null(await _settings.GetSettingAsync<SecSwitchSettings>());
    }

    [Fact]
    public async Task Disabling_SecSwitch_lifts_a_lockdown_immediately()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();
        await _gate.RefreshAsync();
        Assert.True(await _gate.IsActiveAsync());

        await _controller.Settings(new SecSwitchSettings { Enabled = false, FeedUrl = SecSwitchSettings.DefaultFeedUrl });

        Assert.False(await _gate.IsActiveAsync());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabling_or_going_manual_clears_stale_locks_so_they_cannot_resurface(bool enabled, bool autoApply)
    {
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();

        await _controller.Settings(new SecSwitchSettings { Enabled = enabled, AutoApply = autoApply, FeedUrl = SecSwitchSettings.DefaultFeedUrl });
        Assert.False((await _ledger.GetAsync()).Entries["core-1"].LockedDown);

        await _controller.Settings(new SecSwitchSettings { Enabled = true, AutoApply = true, FeedUrl = SecSwitchSettings.DefaultFeedUrl });
        Assert.False(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task Saving_settings_that_stay_enabled_and_automatic_keeps_the_lockdown()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();
        await _gate.RefreshAsync();

        await _controller.Settings(new SecSwitchSettings { Enabled = true, AutoApply = true, AutoUpdateAll = true, FeedUrl = SecSwitchSettings.DefaultFeedUrl });

        Assert.True(await _gate.IsActiveAsync());
        Assert.True((await _ledger.GetAsync()).Entries["core-1"].LockedDown);
    }

    [Fact]
    public async Task Audit_lists_entries_newest_first()
    {
        var now = DateTimeOffset.UtcNow;
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "old", RecordedAt = now.AddDays(-2) });
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "new", RecordedAt = now });

        var model = Assert.IsType<List<LedgerEntry>>(Assert.IsType<ViewResult>(await _controller.Audit()).Model);

        Assert.Equal(["new", "old"], model.Select(e => e.AdvisoryId));
    }

    [Fact]
    public async Task Suppressing_the_advisory_that_holds_a_lockdown_lifts_it()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();
        await _gate.RefreshAsync();

        var result = await _controller.Suppress("core-1");

        Assert.Equal(nameof(SecSwitchController.Audit), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.True((await _ledger.GetAsync()).Entries["core-1"].Suppressed);
        Assert.False(await _gate.IsActiveAsync());
        Assert.NotNull(_controller.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Fact]
    public async Task Suppressing_an_unknown_advisory_reports_an_error()
    {
        await _controller.Suppress("nope");

        Assert.NotNull(_controller.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Empty((await _ledger.GetAsync()).Entries);
    }

    [Fact]
    public async Task Unsuppressing_forgets_the_entry_and_refuses_one_that_is_not_suppressed()
    {
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "a1", Status = LedgerStatus.Acted });
        await _controller.Unsuppress("a1");
        Assert.NotNull(_controller.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Single((await _ledger.GetAsync()).Entries);

        await _ledger.SuppressAsync("a1");
        await _controller.Unsuppress("a1");

        Assert.Empty((await _ledger.GetAsync()).Entries);
    }

    [Fact]
    public async Task Suppress_confirmation_names_the_advisory_and_posts_to_Suppress()
    {
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "a1", Status = LedgerStatus.Acted });

        var view = Assert.IsType<ViewResult>(await _controller.SuppressConfirm("a1"));

        Assert.Equal("Confirm", view.ViewName);
        var model = Assert.IsType<SecSwitchController.ConfirmActionViewModel>(view.Model);
        Assert.Equal(nameof(SecSwitchController.Suppress), model.FormAction);
        Assert.Equal("a1", model.FieldValue);
        Assert.Contains("a1", model.Description);
    }

    [Fact]
    public async Task Confirmations_for_unknown_or_already_handled_advisories_redirect_with_an_error()
    {
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "a1", Status = LedgerStatus.Acted });

        Assert.IsType<RedirectToActionResult>(await _controller.SuppressConfirm("nope"));
        Assert.IsType<RedirectToActionResult>(await _controller.SuppressConfirm(null!));
        Assert.IsType<RedirectToActionResult>(await _controller.UnsuppressConfirm("a1"));
    }

    [Fact]
    public async Task Unsuppress_confirmation_posts_to_Unsuppress()
    {
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "a1", Status = LedgerStatus.Acted });
        await _ledger.SuppressAsync("a1");

        var view = Assert.IsType<ViewResult>(await _controller.UnsuppressConfirm("a1"));

        Assert.Equal(nameof(SecSwitchController.Unsuppress),
            Assert.IsType<SecSwitchController.ConfirmActionViewModel>(view.Model).FormAction);
    }
}
