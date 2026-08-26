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

    sealed class StubPlugin(string id, Version version) : BTCPayServer.Abstractions.Models.BaseBTCPayServerPlugin
    {
        public override string Identifier => id;
        public override Version Version => version;
    }
}
