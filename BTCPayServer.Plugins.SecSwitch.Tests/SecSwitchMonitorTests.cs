using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BTCPayServer.Plugins.SecSwitch.Tests.Fixtures;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchMonitorTests
{
    readonly FakeSink _sink = new();
    readonly LedgerStore _ledger = new(new FakeSettingsRepository());

    SecSwitchMonitor Monitor() => new(
        new ActionExecutor(_sink, NullLogger<ActionExecutor>.Instance), _sink, _ledger, NullLogger<SecSwitchMonitor>.Instance);

    Task<ProcessResult> Process(
        Advisory[] advisories, InstanceState? state = null, SecSwitchSettings? settings = null, string?[]? unreadable = null)
        => Monitor().ProcessAsync(advisories, unreadable ?? [], state ?? State(), settings ?? Settings(), CancellationToken.None);

    Task<ProcessResult> AutoUpdate(InstanceState? state = null)
        => Monitor().AutoUpdateAsync(state ?? State(), CancellationToken.None);

    async Task<LedgerEntry> EntryFor(string id) => (await _ledger.GetAsync()).Entries[id];

    static Advisory CoreAdvisory(string? fixedVersion = null) =>
        Adv(id: "core-1", identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: fixedVersion);

    [Fact]
    public async Task An_applicable_plugin_advisory_is_acted_on_and_recorded()
    {
        var result = await Process([Adv()]);

        Assert.Equal(["Plug"], _sink.Disabled);
        Assert.True(result.RestartNeeded);
        Assert.Equal(0, _sink.Stops);
        var entry = Assert.Single(result.Recorded);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.Equal("DisablePlugin", entry.Action);
        Assert.False(entry.LockedDown);
        Assert.Equal(entry.AdvisoryId, (await EntryFor("adv-1")).AdvisoryId);
    }

    [Fact]
    public async Task An_advisory_is_not_acted_on_twice()
    {
        await Process([Adv()]);

        var second = await Process([Adv()]);

        Assert.Single(_sink.Disabled);
        Assert.Empty(second.Recorded);
        Assert.False(second.RestartNeeded);
    }

    [Fact]
    public async Task Advisories_that_do_not_apply_are_neither_recorded_nor_acted_on()
    {
        var result = await Process([Adv()], State("2.0.0"));

        Assert.Empty(result.Recorded);
        Assert.Empty((await _ledger.GetAsync()).Entries);
        Assert.Empty(_sink.Disabled);
    }

    [Fact]
    public async Task A_plugin_installed_after_its_advisory_was_published_is_still_caught()
    {
        await Process([Adv()], State(pluginId: "Other"));
        Assert.Empty(_sink.Disabled);

        await Process([Adv()], State());

        Assert.Equal(["Plug"], _sink.Disabled);
    }

    [Fact]
    public async Task Manual_mode_holds_the_advisory_for_a_decision_and_acts_on_nothing()
    {
        var result = await Process([Adv()], settings: Settings(autoApply: false));

        Assert.Empty(_sink.Disabled);
        Assert.False(result.RestartNeeded);
        var entry = Assert.Single(result.Recorded);
        Assert.Equal(LedgerStatus.NeedsDecision, entry.Status);
        Assert.Equal("Notify", entry.Action);
    }

    [Fact]
    public async Task Switching_to_automatic_acts_on_an_advisory_held_in_manual_mode()
    {
        await Process([Adv()], settings: Settings(autoApply: false));

        await Process([Adv()]);

        Assert.Equal(["Plug"], _sink.Disabled);
        Assert.Equal(LedgerStatus.Acted, (await EntryFor("adv-1")).Status);
    }

    [Fact]
    public async Task A_failed_action_needs_attention_and_is_retried_on_the_next_poll()
    {
        _sink.QueueDisableResult = false;
        var first = await Process([Adv()]);
        Assert.Equal(LedgerStatus.NeedsAttention, Assert.Single(first.Recorded).Status);
        Assert.False(first.RestartNeeded);

        _sink.QueueDisableResult = true;
        var second = await Process([Adv()]);

        Assert.Equal(LedgerStatus.Acted, Assert.Single(second.Recorded).Status);
        Assert.True(second.RestartNeeded);
    }

    [Fact]
    public async Task An_unchanged_failure_is_retried_but_not_recorded_again()
    {
        _sink.QueueDisableResult = false;
        await Process([Adv()]);

        var second = await Process([Adv()]);

        Assert.Equal(2, _sink.Disabled.Count);
        Assert.Empty(second.Recorded);
    }

    [Fact]
    public async Task A_suppressed_advisory_is_skipped()
    {
        _sink.QueueDisableResult = false;
        await Process([Adv()]);
        await _ledger.SuppressAsync("adv-1");
        _sink.QueueDisableResult = true;
        _sink.Disabled.Clear();

        var result = await Process([Adv()]);

        Assert.Empty(_sink.Disabled);
        Assert.Empty(result.Recorded);
    }

    [Fact]
    public async Task An_unknown_core_version_needs_attention_and_never_locks_down()
    {
        var result = await Process([CoreAdvisory()], State(coreVersion: null));

        var entry = Assert.Single(result.Recorded);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.False(entry.LockedDown);
    }

    [Fact]
    public async Task A_core_advisory_locks_the_server_down_for_as_long_as_it_applies()
    {
        var first = await Process([CoreAdvisory()]);
        var second = await Process([CoreAdvisory()]);

        var entry = Assert.Single(first.Recorded);
        Assert.Equal("Lockdown", entry.Action);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.True(entry.LockedDown);
        Assert.False(first.RestartNeeded);
        Assert.Empty(second.Recorded);
        Assert.True((await EntryFor("core-1")).LockedDown);
    }

    [Fact]
    public async Task A_core_advisory_with_a_fix_and_host_update_support_updates_and_stays_locked_down()
    {
        var result = await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        Assert.False(result.RestartNeeded);
        var entry = Assert.Single(result.Recorded);
        Assert.Equal("UpdateCore", entry.Action);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task A_lockdown_becomes_a_core_update_once_the_host_can_update_without_lifting_the_lockdown()
    {
        await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: false));
        Assert.Equal(0, _sink.CoreUpdates);

        await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        var entry = await EntryFor("core-1");
        Assert.Equal("UpdateCore", entry.Action);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task A_failed_core_update_keeps_the_lockdown()
    {
        _sink.CoreUpdateThrows = new InvalidOperationException("host update failed");

        await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true));

        var entry = await EntryFor("core-1");
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task A_lockdown_is_resolved_once_the_core_version_is_no_longer_affected()
    {
        await Process([CoreAdvisory()]);

        var result = await Process([CoreAdvisory()], State(coreVersion: "9.0.1"));

        var entry = Assert.Single(result.Recorded);
        Assert.Equal(LedgerStatus.Resolved, entry.Status);
        Assert.False(entry.LockedDown);
        Assert.False((await EntryFor("core-1")).LockedDown);
    }

    [Fact]
    public async Task A_lockdown_is_resolved_when_the_advisory_leaves_the_feed()
    {
        await Process([CoreAdvisory()]);

        var result = await Process([]);

        var entry = Assert.Single(result.Recorded);
        Assert.Equal(LedgerStatus.Resolved, entry.Status);
        Assert.False(entry.LockedDown);
    }

    [Fact]
    public async Task Manual_mode_lifts_a_lockdown()
    {
        await Process([CoreAdvisory()]);

        await Process([CoreAdvisory()], settings: Settings(autoApply: false));

        var entry = await EntryFor("core-1");
        Assert.Equal(LedgerStatus.NeedsDecision, entry.Status);
        Assert.False(entry.LockedDown);
    }

    [Fact]
    public async Task An_unreadable_feed_entry_keeps_the_lockdown_it_was_holding()
    {
        await Process([CoreAdvisory()]);

        var result = await Process([], unreadable: ["core-1"]);

        Assert.Empty(result.Recorded);
        Assert.True((await EntryFor("core-1")).LockedDown);
    }

    [Fact]
    public async Task An_unreadable_entry_without_an_id_does_not_stand_in_for_the_missing_advisory()
    {
        await Process([CoreAdvisory()]);

        var result = await Process([], unreadable: [null]);

        Assert.Equal(LedgerStatus.Resolved, Assert.Single(result.Recorded).Status);
    }

    [Fact]
    public async Task A_core_update_is_dispatched_again_only_after_the_window_has_passed()
    {
        var state = State(canUpdateCore: true);
        await Process([CoreAdvisory("9.0.0")], state);
        await Process([CoreAdvisory("9.0.0")], state);
        Assert.Equal(1, _sink.CoreUpdates);

        var entry = await EntryFor("core-1");
        entry.RecordedAt = DateTimeOffset.UtcNow - SecSwitchMonitor.CoreUpdateWindow - TimeSpan.FromMinutes(1);
        await _ledger.RecordAsync(entry);
        var retry = await Process([CoreAdvisory("9.0.0")], state);

        Assert.Equal(2, _sink.CoreUpdates);
        Assert.Single(retry.Recorded);
        Assert.True((await EntryFor("core-1")).RecordedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task An_advisory_core_update_is_not_repeated_by_auto_update_in_the_same_poll()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        var state = State(canUpdateCore: true);

        await Process([CoreAdvisory("9.0.0")], state);
        await AutoUpdate(state);

        Assert.Equal(1, _sink.CoreUpdates);
    }

    [Fact]
    public async Task An_auto_update_of_core_is_not_repeated_by_an_advisory_but_the_advisory_still_locks_down()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        var state = State(canUpdateCore: true);

        await AutoUpdate(state);
        await Process([CoreAdvisory("9.0.0")], state);

        Assert.Equal(1, _sink.CoreUpdates);
        var entry = await EntryFor("core-1");
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task Two_core_advisories_in_one_feed_dispatch_a_single_update()
    {
        var second = Adv(id: "core-2", identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: "9.0.0");

        await Process([CoreAdvisory("9.0.0"), second], State(canUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        Assert.True((await EntryFor("core-2")).LockedDown);
    }

    [Fact]
    public async Task An_auto_update_of_core_is_retried_after_the_window_has_passed()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        await AutoUpdate(State(canUpdateCore: true));
        var entry = await EntryFor("autoupdate:BTCPayServer@2.5.0");
        entry.RecordedAt = DateTimeOffset.UtcNow - SecSwitchMonitor.CoreUpdateWindow - TimeSpan.FromMinutes(1);
        await _ledger.RecordAsync(entry);

        await AutoUpdate(State(canUpdateCore: true));

        Assert.Equal(2, _sink.CoreUpdates);
    }

    [Fact]
    public async Task A_plugin_auto_update_is_never_retried_for_the_same_version()
    {
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];
        await AutoUpdate();
        var entry = await EntryFor("autoupdate:Plug@1.3.0");
        entry.RecordedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
        await _ledger.RecordAsync(entry);

        await AutoUpdate();

        Assert.Single(_sink.Updated);
    }

    [Fact]
    public async Task Auto_update_updates_a_plugin_once_per_version_and_asks_for_a_restart()
    {
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];

        var first = await AutoUpdate();
        var second = await AutoUpdate();

        Assert.Equal([("Plug", "1.3.0")], _sink.Updated);
        Assert.True(first.RestartNeeded);
        Assert.False(second.RestartNeeded);
        Assert.Equal(LedgerStatus.Acted, (await EntryFor("autoupdate:Plug@1.3.0")).Status);
    }

    [Fact]
    public async Task Auto_update_retries_a_failed_update_without_a_restart_and_never_disables()
    {
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];
        _sink.QueueUpdateResult = false;

        var failed = await AutoUpdate();
        _sink.QueueUpdateResult = true;
        var retried = await AutoUpdate();

        Assert.False(failed.RestartNeeded);
        Assert.Empty(_sink.Disabled);
        Assert.True(retried.RestartNeeded);
        Assert.Equal(LedgerStatus.Acted, (await EntryFor("autoupdate:Plug@1.3.0")).Status);
    }

    [Fact]
    public async Task Auto_update_skips_a_suppressed_update()
    {
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];
        _sink.QueueUpdateResult = false;
        await AutoUpdate();
        await _ledger.SuppressAsync("autoupdate:Plug@1.3.0");
        _sink.Updated.Clear();

        await AutoUpdate();

        Assert.Empty(_sink.Updated);
    }

    [Fact]
    public async Task Auto_update_updates_core_once_when_the_host_can_update_and_a_newer_release_exists()
    {
        _sink.LatestCore = new Version(2, 5, 0);

        await AutoUpdate(State(canUpdateCore: true));
        await AutoUpdate(State(canUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        Assert.Equal(LedgerStatus.Acted, (await EntryFor("autoupdate:BTCPayServer@2.5.0")).Status);
    }

    [Theory]
    [InlineData(false, "2.5.0")]
    [InlineData(true, "2.4.7")]
    [InlineData(true, "2.4.0")]
    [InlineData(true, null)]
    public async Task Auto_update_leaves_core_alone_without_host_support_or_a_newer_release(bool canUpdate, string? latest)
    {
        _sink.LatestCore = latest is null ? null : Version.Parse(latest);

        await AutoUpdate(State(canUpdateCore: canUpdate));

        Assert.Equal(0, _sink.CoreUpdates);
    }
}
