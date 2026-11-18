using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class PeriodicTaskTests
{
    sealed class StubPlugin(string id, Version version) : BaseBTCPayServerPlugin
    {
        public override string Identifier => id;
        public override Version Version => version;
    }

    [Fact]
    public async Task A_pending_SecSwitch_change_can_restart_after_the_feed_URL_is_cleared()
    {
        var settings = new FakeSettingsRepository();
        var ledger = new LedgerStore(settings);
        await ledger.RecordAsync(new LedgerEntry
        {
            AdvisoryId = "affected-plugin",
            Identifier = "Plug",
            Action = nameof(SecSwitchAction.DisablePlugin),
            Status = LedgerStatus.Acted,
            RecordedAt = DateTimeOffset.UtcNow
        });
        var sink = new FakeSink { Pending = ["Plug"] };
        var executor = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var monitor = new SecSwitchMonitor(executor, sink, ledger, NullLogger<SecSwitchMonitor>.Instance);
        var lockdown = new LockdownGate(settings, ledger, NullLogger<LockdownGate>.Instance);
        var task = new SecSwitchPeriodicTask(settings, null!, monitor, ledger, executor, lockdown, null!, [], null!, sink,
            NullLogger<SecSwitchPeriodicTask>.Instance);

        await task.Do(TestContext.Current.CancellationToken);

        Assert.Equal(1, sink.Stops);
    }

    [Fact]
    public void BuildState_maps_installed_plugins_case_insensitively()
    {
        var state = SecSwitchPeriodicTask.BuildState([new StubPlugin("Alpha", new Version(1, 0, 0))], "2.4.7", canUpdateCore: true);

        Assert.Equal(new Version(1, 0, 0), state.InstalledPlugins["alpha"]);
        Assert.True(state.CanUpdateCore);
    }

    [Theory]
    [InlineData("2.4.7", "2.4.7")]
    [InlineData("v2.4.7", "2.4.7")]
    [InlineData("v2.4.7+abc123", "2.4.7")]
    [InlineData("2.4.7-rc1", "2.4.7")]
    [InlineData("v2.4.7.1+meta", "2.4.7.1")]
    public void BuildState_reduces_the_core_version_to_its_numeric_part(string raw, string expected)
    {
        Assert.Equal(Version.Parse(expected), SecSwitchPeriodicTask.BuildState([], raw, false).CoreVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("dev")]
    public void BuildState_leaves_an_unparseable_core_version_unknown_instead_of_zero(string? raw)
    {
        Assert.Null(SecSwitchPeriodicTask.BuildState([], raw, false).CoreVersion);
    }

    [Fact]
    public void BuildState_skips_plugins_without_an_identifier()
    {
        var state = SecSwitchPeriodicTask.BuildState(
            [new StubPlugin("", new Version(1, 0, 0)), new StubPlugin("Beta", new Version(2, 0, 0))], "2.4.7", false);

        Assert.Equal("Beta", Assert.Single(state.InstalledPlugins).Key);
    }
}
