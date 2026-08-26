using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class PeriodicTaskTests
{
    [Fact]
    public void Build_state_maps_installed_plugins_and_core_version()
    {
        var state = SecSwitchPeriodicTask.BuildState(
            [new StubPlugin("Alpha", new Version(1, 2, 3))], "2.4.2", canUseSsh: true);

        Assert.Equal(new Version(1, 2, 3), state.InstalledPlugins["Alpha"]);
        Assert.Equal(new Version(2, 4, 2), state.CoreVersion);
        Assert.True(state.CanUseSsh);
    }

    [Theory]
    [InlineData("v2.4.2", "2.4.2")]
    [InlineData("2.4.2+abc123", "2.4.2")]
    [InlineData("v2.4.2+abc123", "2.4.2")]
    public void Core_version_strips_prefix_and_build_metadata(string raw, string expected)
    {
        // Task 13 review, Finding M2 (Minor) fix: this test's own comment previously asserted, as
        // fact, that BTCPayServerEnvironment.Version "carries a leading 'v' and a +buildmeta suffix" -
        // false for THIS build (Build/Version.csproj sets a bare <Version>2.4.2</Version>, no 'v', and
        // there is no SourceLink to append a +sha), even though the code under test is correct: it
        // defensively mirrors PluginService.GetShortBtcpayVersion
        // (BTCPayServer/Plugins/PluginManager/PluginService.cs:61 -
        // Env.Version.TrimStart('v').Split('+')[0]) for whatever a future build configuration might
        // produce. The InlineData values below exercise that defensive stripping directly; they are
        // not a claim about what this build's own version string actually looks like.
        var state = SecSwitchPeriodicTask.BuildState([], raw, canUseSsh: false);
        Assert.Equal(Version.Parse(expected), state.CoreVersion);
    }

    [Fact]
    public void Unparseable_core_version_falls_back_without_throwing()
    {
        var state = SecSwitchPeriodicTask.BuildState([], "not-a-version", canUseSsh: false);
        Assert.NotNull(state.CoreVersion);
    }

    [Fact]
    public void Null_plugin_list_falls_back_to_an_empty_map_without_throwing()
    {
        // BuildState's parameter is typed non-nullable, but that is a compile-time hint only - a
        // caller across an assembly boundary can still pass null (matches this codebase's pervasive
        // "defensive guards mirror the type but do not trust it" posture - see e.g.
        // AdvisoryVerifier.Verify's own doc comment). IEnumerable<IBTCPayServerPlugin> is what core's
        // own DI hands SecSwitchPeriodicTask's constructor; an empty registration set is a real,
        // reachable input shape even without a hostile caller.
        var state = SecSwitchPeriodicTask.BuildState(null!, "2.4.2", canUseSsh: false);
        Assert.Empty(state.InstalledPlugins);
    }

    [Fact]
    public void Null_element_in_plugin_list_is_skipped_without_throwing()
    {
        var state = SecSwitchPeriodicTask.BuildState(
            [null!, new StubPlugin("Alpha", new Version(1, 0, 0))], "2.4.2", canUseSsh: false);

        Assert.Single(state.InstalledPlugins);
        Assert.Equal(new Version(1, 0, 0), state.InstalledPlugins["Alpha"]);
    }

    // --- Task 13 review, Finding I2 (Important) fix: BuildState grew a 4th parameter,
    // sshVerificationPending, distinguishing "SSH not configured" from "SSH configured but not yet
    // verified" - see InstanceState.SshVerificationPending's own doc comment for the full reasoning.

    [Fact]
    public void Build_state_carries_ssh_verification_pending_through_to_instance_state()
    {
        var state = SecSwitchPeriodicTask.BuildState([], "2.4.2", canUseSsh: false, sshVerificationPending: true);
        Assert.True(state.SshVerificationPending);
        Assert.False(state.CanUseSsh);
    }

    [Fact]
    public void Build_state_defaults_ssh_verification_pending_to_false()
    {
        // Every call site written before this parameter existed (including several in this very
        // file, above) omits it - must keep its original, unambiguous meaning of "nothing pending".
        var state = SecSwitchPeriodicTask.BuildState([], "2.4.2", canUseSsh: false);
        Assert.False(state.SshVerificationPending);
    }

    // --- Task 13 review, Finding I4 (Important): hard requirement 1 ("pass recorded CONTENT HASHES
    // to FetchAsync, not advisory ids") had no test coverage at all - BuildKnownContentHashes is the
    // one line whose regression re-downloads every advisory on every poll, forever.

    static LedgerEntry Entry(string contentHash) => new() { AdvisoryId = "a", ContentHash = contentHash };

    [Fact]
    public void Known_content_hashes_are_built_from_ledger_entries()
    {
        var ledger = new SecSwitchLedger
        {
            Entries =
            {
                ["a1"] = Entry("hash-a1"),
                ["a2"] = Entry("hash-a2")
            }
        };

        var known = SecSwitchPeriodicTask.BuildKnownContentHashes(ledger);

        Assert.Equal(2, known.Count);
        Assert.Contains("hash-a1", known);
        Assert.Contains("hash-a2", known);
    }

    [Fact]
    public void Known_content_hashes_skip_withheld_empty_hashes()
    {
        // A non-terminal or SignaturesComplete=false entry has its ContentHash withheld (see
        // SecSwitchMonitor.RecordAsync's own doc comment) - an empty string must never be treated as
        // a "known" hash, or AdvisoryFetcher's own known.Contains(entry.ContentHash) check
        // (AdvisoryFetcher.cs) would never match a blank feed entry either way, but including "" here
        // would be a meaningless, misleading entry in the returned set regardless.
        var ledger = new SecSwitchLedger
        {
            Entries =
            {
                ["a1"] = Entry(""),
                ["a2"] = Entry("hash-a2")
            }
        };

        var known = SecSwitchPeriodicTask.BuildKnownContentHashes(ledger);

        var hash = Assert.Single(known);
        Assert.Equal("hash-a2", hash);
    }

    [Fact]
    public void Known_content_hashes_are_never_advisory_ids()
    {
        // Hard requirement 1's exact failure mode, pinned directly: the dictionary KEY (the advisory
        // id) must never leak into the returned set - only the persisted ContentHash values.
        var ledger = new SecSwitchLedger
        {
            Entries = { ["advisory-id-should-never-appear"] = Entry("hash-a1") }
        };

        var known = SecSwitchPeriodicTask.BuildKnownContentHashes(ledger);

        Assert.DoesNotContain("advisory-id-should-never-appear", known);
        Assert.Contains("hash-a1", known);
    }

    [Fact]
    public void Known_content_hashes_from_an_empty_ledger_is_an_empty_set()
    {
        var known = SecSwitchPeriodicTask.BuildKnownContentHashes(new SecSwitchLedger());
        Assert.Empty(known);
    }

    [Fact]
    public void Known_content_hashes_from_a_null_ledger_does_not_throw()
    {
        var known = SecSwitchPeriodicTask.BuildKnownContentHashes(null!);
        Assert.Empty(known);
    }

    // --- Task 13 review round 2, Finding R1 (Important) fix: NeedsAttention joined the notify-worthy
    // set alongside Acted and NeedsDecision, so a deferred fixable core advisory (SSH configured but
    // not yet verified - see PolicyResolver.SshVerificationPendingPhrase) is surfaced on the FIRST
    // poll rather than only in an audit log nobody is watching, given
    // CheckConfigurationHostedService's probe can retry forever and never succeed. This also closes
    // the identical, pre-existing silence for the indeterminate-installed-version case, which shares
    // this same status.

    [Theory]
    [InlineData(LedgerStatus.Acted)]
    [InlineData(LedgerStatus.NeedsDecision)]
    [InlineData(LedgerStatus.NeedsAttention)]
    public void Notifiable_statuses_are_notified(string status)
    {
        Assert.True(SecSwitchPeriodicTask.IsNotifiable(status));
    }

    [Theory]
    [InlineData(LedgerStatus.Rejected)]
    [InlineData(LedgerStatus.Unverified)]
    [InlineData(LedgerStatus.NotApplicable)]
    [InlineData(LedgerStatus.Suppressed)]
    [InlineData(LedgerStatus.Unsuppressed)]
    [InlineData("")]
    public void Non_notifiable_statuses_are_not_notified(string status)
    {
        // Regression guard: Rejected/Unverified retry automatically without needing an admin to do
        // anything differently, NotApplicable affirmatively means "not affected", and
        // Suppressed/Unsuppressed are the admin's own escape-hatch bookkeeping - none of these should
        // have started being notified as a side effect of this fix.
        Assert.False(SecSwitchPeriodicTask.IsNotifiable(status));
    }

    // ================================================================================
    // Final whole-branch review, Finding I4 (Important): a stuck NeedsAttention advisory notified on
    // EVERY poll, forever. It is notifiable but not terminal, so IsActedAsync never latches it and it
    // is re-recorded hourly - and core's NotificationSender inserts a new row per admin per call with
    // no dedupe of its own. Unbounded table growth, plus a bell an admin learns to ignore, degrading
    // the very signal the NeedsAttention notification was added to provide. Notification is now gated
    // on the state being NEW or CHANGED; visibility in the banner and audit log is untouched (both
    // read the ledger directly, not this predicate).
    // ================================================================================

    static LedgerEntry Recorded(string id, string status, string reason = "r") => new()
    { AdvisoryId = id, Status = status, Reason = reason, RecordedAt = DateTimeOffset.UtcNow };

    static Dictionary<string, SecSwitchPeriodicTask.NotifiedState> Prior(
        string id, string status, string reason = "r") =>
        new(StringComparer.OrdinalIgnoreCase)
        { [id] = new SecSwitchPeriodicTask.NotifiedState(status, reason) };

    [Fact]
    public void A_newly_recorded_notifiable_advisory_is_notified()
    {
        Assert.True(SecSwitchPeriodicTask.ShouldNotify(
            Recorded("a1", LedgerStatus.NeedsAttention),
            new Dictionary<string, SecSwitchPeriodicTask.NotifiedState>()));
    }

    [Fact]
    public void An_unchanged_needs_attention_advisory_is_not_notified_again()
    {
        // The headline case: an SSH probe that never succeeds, an installed version that stays
        // indeterminate, or an action that keeps failing to queue, re-recorded identically every hour.
        Assert.False(SecSwitchPeriodicTask.ShouldNotify(
            Recorded("a1", LedgerStatus.NeedsAttention, "SSH connectivity has not finished verifying"),
            Prior("a1", LedgerStatus.NeedsAttention, "SSH connectivity has not finished verifying")));
    }

    [Fact]
    public void A_needs_attention_advisory_whose_reason_changed_is_notified_again()
    {
        // Something about the situation genuinely moved - e.g. an indeterminate version became an SSH
        // deferral, or a queue failure became a different failure. Worth re-announcing.
        Assert.True(SecSwitchPeriodicTask.ShouldNotify(
            Recorded("a1", LedgerStatus.NeedsAttention, "Failed to queue disable of Plug"),
            Prior("a1", LedgerStatus.NeedsAttention, "SSH connectivity has not finished verifying")));
    }

    [Fact]
    public void An_advisory_whose_status_changed_is_notified_again()
    {
        // NeedsAttention -> Acted is exactly the transition an admin most wants to hear about.
        Assert.True(SecSwitchPeriodicTask.ShouldNotify(
            Recorded("a1", LedgerStatus.Acted),
            Prior("a1", LedgerStatus.NeedsAttention)));
    }

    [Fact]
    public void A_non_notifiable_status_is_never_notified_however_new_it_is()
    {
        // The dedupe must narrow the set, never widen it: Rejected/Unverified/NotApplicable stay out
        // regardless of whether they are new.
        Assert.False(SecSwitchPeriodicTask.ShouldNotify(
            Recorded("a1", LedgerStatus.Unverified),
            new Dictionary<string, SecSwitchPeriodicTask.NotifiedState>()));
    }

    [Fact]
    public void Prior_state_lookup_is_case_insensitive_on_the_advisory_id()
    {
        // LedgerStore keys entries case-insensitively (a differently-cased id resolves to the SAME
        // row), so a case difference must not read as "never seen before" and re-notify.
        Assert.False(SecSwitchPeriodicTask.ShouldNotify(
            Recorded("A1", LedgerStatus.NeedsAttention),
            Prior("a1", LedgerStatus.NeedsAttention)));
    }

    [Fact]
    public void Should_notify_tolerates_null_inputs_without_throwing()
    {
        Assert.False(SecSwitchPeriodicTask.ShouldNotify(null!, new Dictionary<string, SecSwitchPeriodicTask.NotifiedState>()));
        Assert.True(SecSwitchPeriodicTask.ShouldNotify(Recorded("a1", LedgerStatus.NeedsAttention), null!));
    }

    [Fact]
    public void Prior_notification_states_snapshot_status_and_reason_by_value()
    {
        // Taken by VALUE, deliberately: the snapshot comes from the same SecSwitchLedger instance
        // LedgerStore.RecordAsync may later mutate in place, so holding the LedgerEntry objects
        // themselves would compare an entry against its own updated self and never report a change -
        // silently restoring the every-poll notification this fix removes.
        var entry = Recorded("a1", LedgerStatus.NeedsAttention, "before");
        var ledger = new SecSwitchLedger { Entries = { ["a1"] = entry } };

        var snapshot = SecSwitchPeriodicTask.BuildPriorNotificationStates(ledger);
        entry.Status = LedgerStatus.Acted;
        entry.Reason = "after";

        Assert.Equal(LedgerStatus.NeedsAttention, snapshot["a1"].Status);
        Assert.Equal("before", snapshot["a1"].Reason);
        Assert.True(SecSwitchPeriodicTask.ShouldNotify(entry, snapshot)); // the change IS seen
    }

    [Fact]
    public void Prior_notification_states_from_a_null_ledger_does_not_throw()
    {
        Assert.Empty(SecSwitchPeriodicTask.BuildPriorNotificationStates(null!));
    }

    sealed class StubPlugin(string id, Version version) : BTCPayServer.Abstractions.Models.BaseBTCPayServerPlugin
    {
        public override string Identifier => id;
        public override Version Version => version;
    }
}
