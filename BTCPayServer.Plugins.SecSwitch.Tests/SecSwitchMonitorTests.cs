using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BTCPayServer.Plugins.SecSwitch.Tests.Fixtures;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchMonitorTests
{
    readonly FakeSink _sink = new();
    readonly FakeSettingsRepository _settings = new();
    readonly LedgerStore _ledger;

    public SecSwitchMonitorTests() => _ledger = new LedgerStore(_settings);

    SecSwitchMonitor Monitor() => new(
        new ActionExecutor(_sink, NullLogger<ActionExecutor>.Instance), _sink, _ledger, NullLogger<SecSwitchMonitor>.Instance);

    async Task<ProcessResult> Process(
        Advisory[] advisories, InstanceState? state = null, SecSwitchSettings? settings = null, string?[]? unreadable = null)
    {
        settings ??= Settings();
        await _settings.UpdateSetting(settings);
        return await Monitor().ProcessAsync(advisories, unreadable ?? [], state ?? State(), settings, CancellationToken.None);
    }

    Task<ProcessResult> AutoUpdate(InstanceState? state = null, SecSwitchSettings? settings = null, Advisory[]? advisories = null)
        => AutoUpdateWithCancellation(TestContext.Current.CancellationToken, state, settings, advisories);

    async Task<ProcessResult> AutoUpdateWithCancellation(CancellationToken ct,
        InstanceState? state = null, SecSwitchSettings? settings = null, Advisory[]? advisories = null)
    {
        settings ??= Settings(autoUpdatePlugin: true, autoUpdateCore: true);
        await _settings.UpdateSetting(settings);
        return await Monitor().AutoUpdateAsync(state ?? State(), settings, advisories ?? [], ct);
    }

    async Task<LedgerEntry> EntryFor(string id) => (await _ledger.GetAsync()).Entries[id];

    async Task SetCoreUpdateTime(DateTimeOffset at)
    {
        var state = await _ledger.GetAsync();
        state.LastCoreUpdateAt = at;
        await _settings.UpdateSetting(state);
    }

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
    public async Task A_plugin_fix_is_never_installed_without_that_plugins_opt_in()
    {
        await Process([Adv(fixedVersion: "1.2.3")], settings: Settings(autoUpdateCore: true));

        Assert.Empty(_sink.Updated);
        Assert.Equal(["Plug"], _sink.Disabled);
    }

    [Fact]
    public async Task A_verified_manual_fix_stays_pending_even_without_the_plugins_auto_update_opt_in()
    {
        _sink.VerifiedQueuedUpdate = true;
        _sink.Pending.Add("Plug");

        var result = await Process([Adv(fixedVersion: "1.2.3")]);

        Assert.Empty(_sink.Updated);
        Assert.Empty(_sink.Disabled);
        Assert.False(result.RestartNeeded);
        Assert.Equal(LedgerStatus.NeedsDecision, (await EntryFor("adv-1")).Status);
    }

    [Fact]
    public async Task A_plugin_can_update_with_the_kill_switch_off_but_an_update_failure_cannot_disable_it()
    {
        var settings = Settings(autoApply: false, autoUpdatePlugin: true);
        var advisories = new[] { Adv(fixedVersion: "1.2.3") };
        _sink.QueueUpdateResult = false;
        await Process(advisories, settings: settings);
        Assert.Empty(_sink.Disabled);
        Assert.Equal(LedgerStatus.NeedsAttention, (await EntryFor("adv-1")).Status);

        _sink.QueueUpdateResult = true;
        await Process(advisories, settings: settings);
        Assert.Equal(LedgerStatus.Acted, (await EntryFor("adv-1")).Status);
        Assert.Empty(_sink.Disabled);
    }

    [Fact]
    public async Task Switching_off_the_kill_switch_during_a_download_prevents_a_fallback_disable()
    {
        _sink.BeforePluginQueue = () => _settings.UpdateSetting(new SecSwitchSettings
        {
            AutoApply = false,
            AutoUpdatePlugins = ["Plug"],
            PolicyRevision = Guid.NewGuid()
        });
        _sink.QueueUpdateResult = false;
        var result = await Process([Adv(fixedVersion: "1.2.3")], settings: Settings(autoUpdatePlugin: true));

        Assert.False(result.RestartNeeded);
        Assert.Empty(_sink.Disabled);
    }

    [Fact]
    public async Task Revoking_a_plugins_update_opt_in_during_a_download_prevents_commit()
    {
        _sink.BeforePluginQueue = () => _settings.UpdateSetting(new SecSwitchSettings { PolicyRevision = Guid.NewGuid() });
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];

        var result = await AutoUpdate(settings: Settings(autoUpdatePlugin: true));

        Assert.False(result.RestartNeeded);
        Assert.Empty(_sink.Updated);
    }

    [Fact]
    public async Task A_fix_affected_by_another_advisory_is_not_installed()
    {
        await Process([Adv(fixedVersion: "1.2.3"), Adv(id: "later", affected: ">=1.2.3 && <1.3.0")],
            settings: Settings(autoUpdatePlugin: true));

        Assert.Empty(_sink.Updated);
        Assert.Equal(["Plug"], _sink.Disabled);
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
        var result = await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true), Settings(autoUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        Assert.False(result.RestartNeeded);
        var entry = Assert.Single(result.Recorded);
        Assert.Equal("UpdateCore", entry.Action);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task An_opted_in_core_update_does_not_lock_down_when_the_kill_switch_is_off()
    {
        var result = await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true),
            Settings(autoApply: false, autoUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        Assert.True(result.CoreUpdateDispatched);
        Assert.False(Assert.Single(result.Recorded).LockedDown);
    }

    [Fact]
    public async Task Turning_on_the_kill_switch_after_a_core_dispatch_preserves_the_cooldown()
    {
        var state = State(canUpdateCore: true);
        var advisories = new[] { CoreAdvisory("9.0.0") };
        await Process(advisories, state, Settings(autoApply: false, autoUpdateCore: true));
        await Process(advisories, state, Settings(autoUpdateCore: true));
        await Process(advisories, state, Settings(autoUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        Assert.True((await EntryFor("core-1")).LockedDown);
    }

    [Fact]
    public async Task A_lockdown_becomes_a_core_update_once_the_host_can_update_without_lifting_the_lockdown()
    {
        await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: false), Settings(autoUpdateCore: true));
        Assert.Equal(0, _sink.CoreUpdates);

        await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true), Settings(autoUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        var entry = await EntryFor("core-1");
        Assert.Equal("UpdateCore", entry.Action);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task Only_a_core_update_that_really_started_is_reported_as_dispatched()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        var state = State(canUpdateCore: true);

        var advisory = await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));
        var auto = await AutoUpdate(state);
        var held = await Process([Adv(id: "core-2", identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: "9.0.0")], state, Settings(autoUpdateCore: true));

        Assert.True(advisory.CoreUpdateDispatched);
        Assert.False(auto.CoreUpdateDispatched);
        Assert.False(held.CoreUpdateDispatched);
        Assert.Equal(1, _sink.CoreUpdates);
    }

    [Fact]
    public async Task A_failed_core_update_is_not_reported_as_dispatched()
    {
        _sink.CoreUpdateThrows = new InvalidOperationException("host update failed");

        var result = await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true), Settings(autoUpdateCore: true));

        Assert.False(result.CoreUpdateDispatched);
    }

    [Fact]
    public async Task An_auto_update_of_core_that_started_is_reported_as_dispatched()
    {
        _sink.LatestCore = new Version(2, 5, 0);

        var result = await AutoUpdate(State(canUpdateCore: true));

        Assert.True(result.CoreUpdateDispatched);
    }

    [Fact]
    public async Task Cancellation_after_reading_the_latest_core_version_does_not_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        _sink.LatestCore = new Version(2, 5, 0);
        _sink.OnReadLatestCore = cancellation.Cancel;

        var result = await AutoUpdateWithCancellation(cancellation.Token, State(canUpdateCore: true));

        Assert.False(result.CoreUpdateDispatched);
        Assert.Equal(0, _sink.CoreUpdates);
    }

    [Fact]
    public async Task An_advisory_that_found_a_recent_core_update_is_a_lockdown_entry_and_does_not_extend_the_window()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        var state = State(canUpdateCore: true);
        await AutoUpdate(state);

        var skipped = await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));

        Assert.False(skipped.CoreUpdateDispatched);
        var held = await EntryFor("core-1");
        Assert.Equal(nameof(SecSwitchAction.Lockdown), held.Action);
        Assert.Equal(LedgerStatus.Acted, held.Status);
        Assert.True(held.LockedDown);

        var auto = await EntryFor("autoupdate:BTCPayServer@2.5.0");
        auto.RecordedAt = DateTimeOffset.UtcNow - SecSwitchMonitor.CoreUpdateWindow - TimeSpan.FromMinutes(1);
        await _ledger.RecordAsync(auto);
        await SetCoreUpdateTime(auto.RecordedAt);
        var retry = await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));

        Assert.True(retry.CoreUpdateDispatched);
        Assert.Equal(2, _sink.CoreUpdates);
        Assert.Equal(nameof(SecSwitchAction.UpdateCore), (await EntryFor("core-1")).Action);
    }

    [Theory]
    [InlineData("DisablePlugin", LedgerStatus.Acted, "Plug", true)]
    [InlineData("UpdatePlugin", LedgerStatus.Acted, "plug", true)]
    [InlineData("DisablePlugin", LedgerStatus.NeedsAttention, "Plug", false)]
    [InlineData("Lockdown", LedgerStatus.Acted, "Plug", false)]
    [InlineData("DisablePlugin", LedgerStatus.Acted, "Other", false)]
    public void A_pending_plugin_change_counts_only_when_SecSwitch_queued_it(string action, string status, string pending, bool expected)
    {
        var entries = new[] { new LedgerEntry { AdvisoryId = "a", Identifier = "Plug", Action = action, Status = status, RecordedAt = DateTimeOffset.UtcNow } };

        Assert.Equal(expected, SecSwitchMonitor.HasPendingChanges([pending], entries, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void Nothing_pending_means_no_restart_is_waited_for()
    {
        var entries = new[] { new LedgerEntry { AdvisoryId = "a", Identifier = "Plug", Action = "DisablePlugin", Status = LedgerStatus.Acted, RecordedAt = DateTimeOffset.UtcNow } };

        Assert.False(SecSwitchMonitor.HasPendingChanges([], entries, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void An_entry_from_before_this_process_started_cannot_still_be_pending()
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var old = new LedgerEntry { AdvisoryId = "a", Identifier = "Plug", Action = "UpdatePlugin", Status = LedgerStatus.Acted, RecordedAt = started.AddDays(-30) };
        var current = new LedgerEntry { AdvisoryId = "b", Identifier = "Plug", Action = "UpdatePlugin", Status = LedgerStatus.Acted, RecordedAt = started.AddMinutes(1) };

        Assert.False(SecSwitchMonitor.HasPendingChanges(["Plug"], [old], started));
        Assert.True(SecSwitchMonitor.HasPendingChanges(["Plug"], [old, current], started));
    }

    [Fact]
    public async Task A_failed_core_update_keeps_the_lockdown()
    {
        _sink.CoreUpdateThrows = new InvalidOperationException("host update failed");

        await Process([CoreAdvisory("9.0.0")], State(canUpdateCore: true), Settings(autoUpdateCore: true));

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
    public async Task An_unreadable_entry_without_an_id_cannot_clear_a_lockdown()
    {
        await Process([CoreAdvisory()]);

        var result = await Process([], unreadable: [null]);

        Assert.Empty(result.Recorded);
        Assert.True((await EntryFor("core-1")).LockedDown);
    }

    [Fact]
    public async Task A_core_update_is_dispatched_again_only_after_the_window_has_passed()
    {
        var state = State(canUpdateCore: true);
        await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));
        await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));
        Assert.Equal(1, _sink.CoreUpdates);

        var entry = await EntryFor("core-1");
        entry.RecordedAt = DateTimeOffset.UtcNow - SecSwitchMonitor.CoreUpdateWindow - TimeSpan.FromMinutes(1);
        await _ledger.RecordAsync(entry);
        await SetCoreUpdateTime(entry.RecordedAt);
        var retry = await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));

        Assert.Equal(2, _sink.CoreUpdates);
        Assert.Single(retry.Recorded);
        Assert.True((await EntryFor("core-1")).RecordedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task An_advisory_core_update_is_not_repeated_by_auto_update_in_the_same_poll()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        var state = State(canUpdateCore: true);

        await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));
        await AutoUpdate(state);

        Assert.Equal(1, _sink.CoreUpdates);
    }

    [Fact]
    public async Task An_auto_update_of_core_is_not_repeated_by_an_advisory_but_the_advisory_still_locks_down()
    {
        _sink.LatestCore = new Version(2, 5, 0);
        var state = State(canUpdateCore: true);

        await AutoUpdate(state);
        await Process([CoreAdvisory("9.0.0")], state, Settings(autoUpdateCore: true));

        Assert.Equal(1, _sink.CoreUpdates);
        var entry = await EntryFor("core-1");
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.True(entry.LockedDown);
    }

    [Fact]
    public async Task Two_core_advisories_in_one_feed_dispatch_a_single_update()
    {
        var second = Adv(id: "core-2", identifier: "BTCPayServer", affected: "<9.0.0", fixedVersion: "9.0.0");

        await Process([CoreAdvisory("9.0.0"), second], State(canUpdateCore: true), Settings(autoUpdateCore: true));

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
        await SetCoreUpdateTime(entry.RecordedAt);

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
    public async Task Routine_updates_do_not_install_an_affected_plugin_version_or_act_on_a_different_opt_in()
    {
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];
        await AutoUpdate(settings: Settings(autoUpdateCore: true));
        await AutoUpdate(advisories: [Adv(affected: ">=1.3.0 && <1.4.0")]);

        Assert.Empty(_sink.Updated);
    }

    [Fact]
    public async Task A_failed_audit_write_keeps_restart_intent_and_can_be_retried_without_queueing_again()
    {
        var settings = Settings(autoUpdatePlugin: true);
        await _settings.UpdateSetting(settings);
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];
        _sink.BeforePluginQueue = () =>
        {
            _settings.RejectLedgerWrites = true;
            return Task.CompletedTask;
        };
        var monitor = Monitor();

        var result = await monitor.AutoUpdateAsync(State(), settings, [], TestContext.Current.CancellationToken);

        Assert.True(result.RestartNeeded);
        Assert.Single(monitor.PendingAuditEntries);
        _settings.RejectLedgerWrites = false;
        await monitor.RetryPendingAuditAsync();
        Assert.Empty(monitor.PendingAuditEntries);
        Assert.Equal(LedgerStatus.Acted, (await EntryFor("autoupdate:Plug@1.3.0")).Status);
        Assert.Single(_sink.Updated);
    }

    [Fact]
    public async Task Routine_updates_defer_a_second_update_that_could_activate_a_conditional_advisory()
    {
        var state = new InstanceState(new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plug"] = new(1, 1, 0),
            ["Other"] = new(1, 1, 0)
        }, null, false);
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0)), new PluginUpdate("Other", new Version(1, 3, 0))];
        var advisory = new Advisory
        {
            Id = "pair",
            Identifier = "Plug",
            AffectedVersions = ">=1.3.0",
            Requires = new AdvisoryDependency("Other", ">=1.3.0"),
            Severity = AdvisorySeverity.High
        };

        await AutoUpdate(state, new SecSwitchSettings { AutoUpdatePlugins = ["Plug", "Other"] }, [advisory]);

        Assert.Equal([("Plug", "1.3.0")], _sink.Updated);
    }

    [Fact]
    public async Task A_newly_queued_conditional_peer_is_checked_again_at_commit()
    {
        _sink.PluginUpdates = [new PluginUpdate("Plug", new Version(1, 3, 0))];
        _sink.BeforePluginQueue = () =>
        {
            _sink.Pending.Add("Other");
            return Task.CompletedTask;
        };
        var advisory = new Advisory
        {
            Id = "pair",
            Identifier = "Plug",
            AffectedVersions = ">=1.3.0",
            Requires = new AdvisoryDependency("Other", ">=1.3.0")
        };
        var state = new InstanceState(new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plug"] = new(1, 1, 0),
            ["Other"] = new(1, 1, 0)
        }, null, false);

        var result = await AutoUpdate(state, Settings(autoUpdatePlugin: true), [advisory]);

        Assert.False(result.RestartNeeded);
        Assert.Empty(_sink.Updated);
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
