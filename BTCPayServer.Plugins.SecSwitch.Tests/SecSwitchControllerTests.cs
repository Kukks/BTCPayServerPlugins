using System.Globalization;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Controllers;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchControllerTests
{
    readonly SettingsRepositoryWithLedgerWriteHook _settings = new();
    readonly LedgerStore _ledger;
    readonly LockdownGate _gate;
    readonly SecSwitchController _controller;

    public SecSwitchControllerTests()
    {
        _ledger = new LedgerStore(_settings);
        _gate = new LockdownGate(_settings, _ledger, NullLogger<LockdownGate>.Instance);
        _controller = NewController();
    }

    SecSwitchController NewController() =>
        new(_settings, _ledger, _gate, NullLogger<SecSwitchController>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };

    sealed class SettingsRepositoryWithLedgerWriteHook : ISettingsRepository
    {
        readonly FakeSettingsRepository _inner = new();
        public Func<Task>? BeforeLedgerWrite { get; set; }

        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class =>
            _inner.GetSettingAsync<T>(name);

        public async Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            if (obj is SecSwitchLedger && BeforeLedgerWrite is { } hook)
                await hook();
            await _inner.UpdateSetting(obj, name);
        }

        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class =>
            _inner.WaitSettingsChanged<T>(cancellationToken);
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

        Assert.True(model.Enabled);
        Assert.Null(model.GetFeedUrl());
        Assert.True(model.AutoApply);
        Assert.False(model.AutoUpdateCore);
        Assert.Empty(model.AutoUpdatePlugins);
    }

    [Fact]
    public async Task Legacy_settings_preserve_the_kill_switch_preference_without_opt_in_to_updates()
    {
        const string legacySettings = """
            {
                "enabled": true,
                "autoApply": false,
                "autoUpdateAll": true,
                "preferUpdateOverDisable": true,
                "feedUrl": "https://raw.githubusercontent.com/btcpayserver/advisories/master/advisories.json"
            }
            """;
        await _settings.UpdateSetting(Newtonsoft.Json.JsonConvert.DeserializeObject<SecSwitchSettings>(legacySettings)!);

        var model = Assert.IsType<SecSwitchSettings>(Assert.IsType<ViewResult>(await _controller.Settings()).Model);
        Assert.True(model.Enabled);
        Assert.False(model.AutoApply);
        Assert.False(model.AutoUpdateCore);
        Assert.Empty(model.AutoUpdatePlugins);
        Assert.Null(model.FeedUrl);
    }

    [Fact]
    public async Task Saving_valid_settings_persists_them_and_redirects()
    {
        var postedRevision = Guid.NewGuid();
        var posted = new SecSwitchSettings
        {
            Enabled = true,
            AutoApply = false,
            FeedUrl = " https://example.test/a.json ",
            AutoUpdateCore = true,
            AutoUpdatePlugins = ["BTCPayServer.Plugins.One", "btcpayserver.plugins.one", ""],
            PolicyRevision = postedRevision
        };

        var result = await _controller.Settings(posted);

        Assert.Equal(nameof(SecSwitchController.Settings), Assert.IsType<RedirectToActionResult>(result).ActionName);
        var saved = (await _settings.GetSettingAsync<SecSwitchSettings>())!;
        Assert.True(saved.Enabled);
        Assert.False(saved.AutoApply);
        Assert.True(saved.AutoUpdateCore);
        Assert.Equal(["BTCPayServer.Plugins.One"], saved.AutoUpdatePlugins);
        Assert.True(saved.IsPluginAutoUpdateEnabled("btcpayserver.plugins.one"));
        Assert.False(saved.IsPluginAutoUpdateEnabled("BTCPayServer.Plugins.Two"));
        Assert.Equal("https://example.test/a.json", saved.FeedUrl);
        Assert.NotNull(saved.PolicyRevision);
        Assert.NotEqual(postedRevision, saved.PolicyRevision);
        Assert.NotNull(_controller.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Theory]
    [InlineData("http://example.test/a.json")]
    [InlineData("/relative")]
    [InlineData("https://raw.githubusercontent.com/btcpayserver/advisories/master/advisories.json")]
    public async Task Saving_an_invalid_or_retired_feed_url_is_rejected(string url)
    {
        var posted = new SecSwitchSettings { Enabled = true, FeedUrl = url };

        var result = await _controller.Settings(posted);

        Assert.Same(posted, Assert.IsType<ViewResult>(result).Model);
        Assert.False(_controller.ModelState.IsValid);
        Assert.Null(await _settings.GetSettingAsync<SecSwitchSettings>());
    }

    [Fact]
    public async Task Mvc_binding_accepts_plugin_checkboxes_and_ignores_posted_policy_revision()
    {
        using var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(SecSwitchSettings));
        var fields = new Dictionary<string, StringValues>
        {
            [nameof(SecSwitchSettings.Enabled)] = "false",
            [nameof(SecSwitchSettings.AutoApply)] = "false",
            [nameof(SecSwitchSettings.AutoUpdateCore)] = new StringValues(["true", "false"]),
            [nameof(SecSwitchSettings.AutoUpdatePlugins)] = new StringValues(["Example.Plugin.One", "Example.Plugin.Two"]),
            [nameof(SecSwitchSettings.PolicyRevision)] = Guid.NewGuid().ToString()
        };
        var settings = await BindFormAsync();
        Assert.False(settings.Enabled);
        Assert.False(settings.AutoApply);
        Assert.True(settings.AutoUpdateCore);
        Assert.Equal(["Example.Plugin.One", "Example.Plugin.Two"], settings.AutoUpdatePlugins);
        Assert.Null(settings.PolicyRevision);
        await _controller.Settings(settings);
        Assert.Equal(settings.AutoUpdatePlugins, (await _settings.GetSettingAsync<SecSwitchSettings>())!.AutoUpdatePlugins);

        fields.Remove(nameof(SecSwitchSettings.AutoUpdatePlugins));
        await _controller.Settings(await BindFormAsync());
        Assert.Empty((await _settings.GetSettingAsync<SecSwitchSettings>())!.AutoUpdatePlugins);

        async Task<SecSwitchSettings> BindFormAsync()
        {
            var context = new ActionContext(new DefaultHttpContext { RequestServices = services }, new RouteData(), new ActionDescriptor());
            var values = new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture);
            var binding = DefaultModelBindingContext.CreateBindingContext(context, values, metadata, bindingInfo: null, modelName: "");
            var binder = services.GetRequiredService<IModelBinderFactory>()
                .CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
            await binder.BindModelAsync(binding);
            Assert.Equal(0, binding.ModelState.ErrorCount);
            return Assert.IsType<SecSwitchSettings>(binding.Result.Model);
        }
    }

    [Fact]
    public async Task Settings_cannot_be_viewed_or_changed_during_lockdown()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();
        await _gate.RefreshAsync();
        Assert.True(await _gate.IsActiveAsync());

        var page = Assert.IsType<StatusCodeResult>(await _controller.Settings());
        var save = Assert.IsType<StatusCodeResult>(await _controller.Settings(new SecSwitchSettings { Enabled = false }));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, page.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, save.StatusCode);
        Assert.True((await _settings.GetSettingAsync<SecSwitchSettings>())!.Enabled);
        Assert.True(await _gate.IsActiveAsync());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabling_checks_or_the_kill_switch_clears_stale_locks(bool enabled, bool autoApply)
    {
        await _settings.UpdateSetting(new SecSwitchSettings { AutoApply = false });
        await Lock();

        await _controller.Settings(new SecSwitchSettings { Enabled = enabled, AutoApply = autoApply });
        Assert.False((await _ledger.GetAsync()).Entries["core-1"].LockedDown);

        await _controller.Settings(new SecSwitchSettings());
        Assert.False(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task Failed_cleanup_leaves_opt_out_effective_and_blocks_reenable_until_cleanup_succeeds()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { AutoApply = false });
        await Lock();
        await _gate.RefreshAsync();
        Assert.False(await _gate.IsActiveAsync());
        _settings.BeforeLedgerWrite = () => throw new InvalidOperationException("Could not persist the ledger.");

        Assert.IsType<RedirectToActionResult>(await _controller.Settings(new SecSwitchSettings { AutoApply = false }));
        var optedOut = (await _settings.GetSettingAsync<SecSwitchSettings>())!;
        Assert.False(optedOut.AutoApply);
        Assert.NotNull(_controller.TempData[WellKnownTempData.ErrorMessage]);
        Assert.False(await _gate.IsActiveAsync());
        Assert.True((await _ledger.GetAsync()).Entries["core-1"].LockedDown);

        var reenable = NewController();
        Assert.IsType<RedirectToActionResult>(await reenable.Settings(new SecSwitchSettings()));
        Assert.NotNull(reenable.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Equal(optedOut.PolicyRevision, (await _settings.GetSettingAsync<SecSwitchSettings>())!.PolicyRevision);
        Assert.False(await _gate.IsActiveAsync());

        _settings.BeforeLedgerWrite = null;
        await NewController().Settings(new SecSwitchSettings());
        var restored = (await _settings.GetSettingAsync<SecSwitchSettings>())!;
        Assert.True(restored.AutoApply);
        Assert.NotEqual(optedOut.PolicyRevision, restored.PolicyRevision);
        Assert.False(await _gate.IsActiveAsync());
        Assert.False((await _ledger.GetAsync()).Entries["core-1"].LockedDown);
    }

    [Fact]
    public async Task Reenable_waits_for_in_progress_opt_out_cleanup()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { AutoApply = false });
        await Lock();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settings.BeforeLedgerWrite = () =>
        {
            cleanupStarted.TrySetResult();
            return releaseCleanup.Task;
        };

        var disable = _controller.Settings(new SecSwitchSettings { AutoApply = false });
        Task<IActionResult>? enable = null;
        try
        {
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            enable = NewController().Settings(new SecSwitchSettings());
            Assert.False(enable.IsCompleted);
            Assert.False((await _settings.GetSettingAsync<SecSwitchSettings>())!.AutoApply);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await disable;
            if (enable is not null)
                await enable;
        }

        Assert.True((await _settings.GetSettingAsync<SecSwitchSettings>())!.AutoApply);
        Assert.False(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task A_settings_write_cannot_replace_the_feed_after_a_lock_has_been_persisted()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { FeedUrl = "https://example.test/a.json" });
        Assert.False(await _gate.IsActiveAsync());
        await Lock();

        var result = Assert.IsType<StatusCodeResult>(await _controller.Settings(new SecSwitchSettings { FeedUrl = "" }));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("https://example.test/a.json", (await _settings.GetSettingAsync<SecSwitchSettings>())!.FeedUrl);

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
    public async Task Audit_uses_recovery_mode_during_lockdown()
    {
        await _settings.UpdateSetting(new SecSwitchSettings());
        await Lock();

        var result = Assert.IsType<ViewResult>(await _controller.Audit());

        Assert.True(Assert.IsType<bool>(result.ViewData["SecSwitchRecoveryMode"]));
    }

    [Fact]
    public async Task Suppressing_the_advisory_that_holds_a_lockdown_lifts_it()
    {
        await _settings.UpdateSetting(new SecSwitchSettings { Enabled = true });
        await Lock();
        await _gate.RefreshAsync();

        var result = await _controller.Suppress("core-1", acknowledgeRisk: true);

        Assert.Equal(nameof(SecSwitchController.Audit), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.True((await _ledger.GetAsync()).Entries["core-1"].Suppressed);
        Assert.False(await _gate.IsActiveAsync());
        Assert.NotNull(_controller.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Fact]
    public async Task Suppression_without_risk_acknowledgement_keeps_the_lockdown()
    {
        await _settings.UpdateSetting(new SecSwitchSettings());
        await Lock();

        var result = Assert.IsType<RedirectToActionResult>(await _controller.Suppress("core-1"));

        Assert.Equal(nameof(SecSwitchController.SuppressConfirm), result.ActionName);
        Assert.Equal("core-1", result.RouteValues!["advisoryId"]);
        Assert.NotNull(_controller.TempData[WellKnownTempData.ErrorMessage]);
        Assert.False((await _ledger.GetAsync()).Entries["core-1"].Suppressed);
        Assert.True(await _gate.IsActiveAsync());
    }

    [Fact]
    public async Task Suppressing_an_unknown_advisory_reports_an_error()
    {
        await _controller.Suppress("nope", acknowledgeRisk: true);

        Assert.NotNull(_controller.TempData[WellKnownTempData.ErrorMessage]);
        Assert.Empty((await _ledger.GetAsync()).Entries);
    }

    [Fact]
    public async Task An_existing_suppression_cannot_be_reversed_during_lockdown()
    {
        await _settings.UpdateSetting(new SecSwitchSettings());
        await Lock();
        await _ledger.RecordAsync(new LedgerEntry { AdvisoryId = "ignored", Suppressed = true });

        var page = Assert.IsType<StatusCodeResult>(await _controller.UnsuppressConfirm("ignored"));
        var save = Assert.IsType<StatusCodeResult>(await _controller.Unsuppress("ignored"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, page.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, save.StatusCode);
        Assert.True((await _ledger.GetAsync()).Entries["ignored"].Suppressed);
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
        Assert.True(model.RequireAcknowledgement);
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
