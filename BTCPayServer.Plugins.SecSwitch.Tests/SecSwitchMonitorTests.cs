using System.Text;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchMonitorTests
{
    static readonly string AdvisoryJson = """
    {"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <2.0.0","fixedVersion":"2.0.0",
     "severity":"critical","title":"Bad","description":"d","references":[],
     "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
    """;

    static byte[] Payload => Encoding.UTF8.GetBytes(AdvisoryJson);

    static InstanceState State() =>
        new(new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.5.0") },
            Version.Parse("2.4.2"), CanUseSsh: false);

    static (SecSwitchMonitor, RecordingSink, LedgerStore) Make(SecSwitchSettings settings)
    {
        var sink = new RecordingSink();
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var monitor = new SecSwitchMonitor(
            new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance),
            ledger, NullLogger<SecSwitchMonitor>.Instance);
        return (monitor, sink, ledger);
    }

    static SecSwitchSettings Settings(params PgpTestKey[] keys) => new()
    {
        Enabled = true,
        QuorumThreshold = 2,
        TrustedKeys = keys.Select(k => new TrustedKey
        { Fingerprint = k.Fingerprint, ArmoredPublicKey = k.ArmoredPublicKey, Identity = "t" }).ToList()
    };

    // Deviation from the Task 11 brief (see task-11-report.md): FetchedAdvisory grew a ContentHash
    // and a SignaturesComplete flag since the brief was written (now a 5-member record, not 3), so
    // this helper takes both explicitly rather than only a params signature array.
    static FetchedAdvisory Fetched(string contentHash, bool signaturesComplete, params string[] signatures)
        => new("a1", contentHash, Payload, signatures, signaturesComplete);

    [Fact]
    public async Task Verified_applicable_advisory_is_acted_on_and_recorded()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var (monitor, sink, ledger) = Make(Settings(a, b));

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), Settings(a, b), CancellationToken.None);

        Assert.Contains("update:Plug:2.0.0", sink.Calls);
        var entry = Assert.Single(entries);
        Assert.Equal("a1", entry.AdvisoryId);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        // SignaturesComplete was true and Acted is terminal, so the content hash IS persisted -
        // see the Complete_but_below_quorum test below for the adversarial converse.
        Assert.Equal("hash-a1", entry.ContentHash);
        Assert.True(await ledger.IsActedAsync("a1"));
    }

    [Fact]
    public async Task Advisory_below_quorum_is_never_acted_on()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload))], State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
        Assert.Equal(LedgerStatus.Unverified, Assert.Single(entries).Status);
    }

    [Fact]
    public async Task Already_handled_advisory_is_not_acted_on_again()
    {
        // Hard requirement 3 / without this, a shutdown advisory would re-fire on every restart -
        // a crash loop, not a kill switch. IsActedAsync is the only thing preventing that.
        //
        // Fixture note (Task 11 review, Finding C1 fix): the pre-recorded entry now carries
        // Status=Acted rather than a blank status. Under the pre-fix code (gated on
        // IsHandledAsync, which latches on ANY recorded entry) a blank status was enough to prove
        // this property; under the fix (gated on IsActedAsync, which latches on TERMINAL statuses
        // only) a blank status is not terminal and would no longer represent "already handled" -
        // see Advisory_latched_as_non_terminal_is_still_acted_on_in_a_later_sweep below, which pins
        // the opposite (and now load-bearing) side of that same distinction.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        await ledger.RecordAsync(new LedgerEntry
        { AdvisoryId = "a1", Status = LedgerStatus.Acted, RecordedAt = DateTimeOffset.UtcNow });

        await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Suppressed_advisory_is_not_acted_on()
    {
        // Task 11 review, Finding C1 fix requirement: "Keep SuppressAsync latching - the admin's
        // escape hatch must stay absolute." Suppressed is one of IsActedAsync's terminal statuses
        // (and, per Finding R2, entry.Suppressed is ALSO checked directly - see LedgerStoreTests
        // for a test that isolates that specific path), so this must hold even under the narrowed
        // gate.
        //
        // Fix-round note (Task 12 review, Finding I3): SuppressAsync no longer fabricates an entry
        // for an id it has never seen, so the advisory is recorded first, matching the new "must
        // already exist" contract (see LedgerStoreTests.Suppressing_an_unknown_advisory_id_is_rejected).
        //
        // Fix-round note (Finding T1 - the load-bearing correction): seeded with the NON-terminal
        // Unverified, not Acted. Acted is itself terminal, so recording it and then suppressing
        // made this fixture byte-identical (in effect) to Already_handled_advisory_is_not_acted_on_again
        // above - Assert.Empty(sink.Calls) would have passed even if SuppressAsync were gutted to a
        // no-op, because IsActedAsync was already true from the Acted seed alone. Seeded
        // non-terminal, IsActedAsync is provably false before SuppressAsync runs, so this test now
        // only passes because suppression itself latches it: a no-op SuppressAsync would leave
        // quorum met and PreferUpdateOverDisable applicable, and the monitor would genuinely queue
        // "update:Plug:2.0.0" and stop - failing Assert.Empty(sink.Calls) below. This is the
        // guarantee this test exists to protect: an admin's suppression of a false positive must
        // actually stop the kill switch from acting, not merely look like it does.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        await ledger.RecordAsync(new LedgerEntry
        { AdvisoryId = "a1", Status = LedgerStatus.Unverified, RecordedAt = DateTimeOffset.UtcNow });
        Assert.True(await ledger.SuppressAsync("a1"));

        await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Unsuppressed_advisory_is_re_evaluated_by_a_later_sweep()
    {
        // Task 12 review, Finding I3, end to end: suppressing an advisory latches IsActedAsync (see
        // Suppressed_advisory_is_not_acted_on above); un-suppressing must not just flip that latch
        // back but make the advisory genuinely reachable again. That also requires ContentHash to
        // have been cleared (see LedgerStore.UnsuppressAsync's own doc comment) - a real
        // AdvisoryFetcher would otherwise keep skipping this advisory's directory by hash regardless
        // of what the ledger's Status says. Feeds the SAME FetchedAdvisory (same ContentHash) back
        // into ProcessAsync three times - acted, suppressed (no-op), unsuppressed (acted again) - to
        // prove the full chain, not just the ledger-level flags in isolation.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);

        var firstSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);
        Assert.Equal(LedgerStatus.Acted, Assert.Single(firstSweep).Status);
        Assert.Equal(["update:Plug:2.0.0", "stop"], sink.Calls);

        Assert.True(await ledger.SuppressAsync("a1"));
        Assert.True(await ledger.IsActedAsync("a1"));

        var suppressedSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);
        Assert.Empty(suppressedSweep); // IsActedAsync still latches - nothing (re-)recorded this sweep
        Assert.Equal(["update:Plug:2.0.0", "stop"], sink.Calls); // no NEW calls since the first sweep

        Assert.True(await ledger.UnsuppressAsync("a1"));
        Assert.False(await ledger.IsActedAsync("a1"));

        var finalSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var finalEntry = Assert.Single(finalSweep);
        Assert.Equal(LedgerStatus.Acted, finalEntry.Status);
        Assert.True(await ledger.IsActedAsync("a1"));
        // Acted a SECOND time - proves genuine re-evaluation, not merely "still present from before".
        Assert.Equal(2, sink.Calls.Count(c => c == "update:Plug:2.0.0"));
    }

    [Fact]
    public async Task Advisory_latched_as_non_terminal_is_still_acted_on_in_a_later_sweep()
    {
        // Reproduces Task 11 review Finding C1 (Critical) end to end: hard requirement 1 withholds
        // ContentHash for an incomplete signature set specifically so AdvisoryFetcher re-downloads
        // it next poll. If THIS method then treated any recorded entry (Rejected, Unverified,
        // NeedsAttention) as "already handled", that re-fetch would arrive here and be turned away
        // anyway - silently, permanently disarming SecSwitch for this advisory id, with no key
        // material needed: a hostile mirror serving malformed bytes, or simply stripping signature
        // files, for a single poll would be enough. Two sweeps for the SAME advisory id: first
        // below quorum (non-terminal), then quorum-met - the second sweep must actually act.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);

        var firstSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: false, a.SignDetached(payload))],
            State(), settings, CancellationToken.None);
        Assert.Equal(LedgerStatus.Unverified, Assert.Single(firstSweep).Status);
        Assert.Empty(sink.Calls);

        var secondSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(secondSweep);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
        Assert.True(await ledger.IsActedAsync("a1"));
    }

    [Theory]
    [InlineData(LedgerStatus.Rejected)]
    [InlineData(LedgerStatus.Unverified)]
    [InlineData(LedgerStatus.NeedsAttention)]
    [InlineData("")]
    public async Task Non_terminal_prior_status_does_not_block_a_later_sweep(string priorStatus)
    {
        // Direct, per-status companion to Advisory_latched_as_non_terminal_is_still_acted_on_in_a_
        // later_sweep above: every status this monitor can itself produce that is final ONLY
        // because it could not evaluate the advisory properly (plus a blank one, for a
        // pre-existing/foreign entry) must leave the advisory eligible for re-evaluation. Does NOT
        // include NotApplicable (Task 11 review, Finding R1 promotes it to terminal - see
        // NotApplicable_prior_status_blocks_a_later_sweep below for the inverted, now-correct
        // expectation for that specific case).
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        await ledger.RecordAsync(new LedgerEntry
        { AdvisoryId = "a1", Status = priorStatus, RecordedAt = DateTimeOffset.UtcNow });

        await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Contains("update:Plug:2.0.0", sink.Calls);
    }

    [Fact]
    public async Task NotApplicable_prior_status_blocks_a_later_sweep()
    {
        // Task 11 review, Finding R1 promotes NotApplicable to a TERMINAL status (see
        // LedgerStatus.NotApplicable's and LedgerStore.IsTerminalStatus's own doc comments):
        // unlike Rejected/Unverified/NeedsAttention, nothing about re-fetching byte-identical
        // advisory.json could change a genuine not-applicable verdict, and caching its ContentHash
        // is what keeps AdvisoryFetcher's own per-poll success budget (MaxAdvisoriesPerPoll) from
        // being permanently exhausted by the ordinary, non-adversarial bulk of advisories that
        // simply do not target anything this instance runs. Re-evaluating a NotApplicable advisory
        // after LOCAL state changes (e.g. installing the affected plugin later) is explicitly out
        // of scope for this task - see task-11-report.md.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        await ledger.RecordAsync(new LedgerEntry
        { AdvisoryId = "a1", Status = LedgerStatus.NotApplicable, RecordedAt = DateTimeOffset.UtcNow });

        await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Unparseable_advisory_is_recorded_as_rejected_without_acting()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var junk = Encoding.UTF8.GetBytes("{ not json");
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", junk, [a.SignDetached(junk), b.SignDetached(junk)], true)],
            State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Rejected, entry.Status);
        // Finding R1: Rejected is final only because SecSwitch could not parse the advisory, never
        // because the content settled anything - must never be cached, or a mirror hostile for a
        // single poll could permanently disarm this advisory id.
        Assert.Equal("", entry.ContentHash);
    }

    // --- Hard requirement 1, refined by Task 11 review Finding R1: an advisory's ContentHash may
    // only be persisted when BOTH the fetch that produced it was complete (SignaturesComplete) AND
    // its resulting Status is terminal "on content" (LedgerStore.IsTerminalStatus - Acted,
    // NeedsDecision, NotApplicable, or Suppressed). AdvisoryFetcher's own hash-based dedup is the
    // OUTER gate and dominates: it skips an index entry outright once its ContentHash is known,
    // before advisory.json or any signature file is ever requested again, and that hash is carried
    // verbatim from the feed's index - never recomputed from the payload - while signatures live in
    // sibling files. So caching for a status that is final only because SecSwitch could NOT look
    // properly (Rejected/Unverified/NeedsAttention) would silently and permanently hide the
    // advisory from every future poll's fetch, not just from this class's own re-action check.

    [Fact]
    public async Task Incomplete_signature_set_is_recorded_but_content_hash_is_withheld()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: false, a.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("", entry.ContentHash);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Incomplete_signature_set_withholds_content_hash_even_when_quorum_is_met()
    {
        // The gate is on SignaturesComplete alone, independent of whether the truncated fetch
        // happened to already carry a quorum-sufficient set: SignaturesComplete=false means the
        // fetcher does not know it saw everything the feed currently offers, so caching this
        // content hash as "seen" is wrong regardless of today's outcome - even a terminal one.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: false, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Acted, entry.Status); // quorum WAS met - proves the gate is not
                                                          // just "unverified => no hash"
        Assert.Equal("", entry.ContentHash);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
    }

    [Fact]
    public async Task Complete_but_below_quorum_advisory_does_not_cache_content_hash()
    {
        // Task 11 review, Finding R1 - THE ADVERSARIAL ROUTE. Supersedes this test's Round-1 name
        // (Complete_signature_set_persists_content_hash_regardless_of_outcome) and its assertion,
        // which asserted the CONTENT HASH WAS cached here - a real defect, not a style choice (see
        // task-11-report.md's Round 2 section for the full reasoning). A mirror hostile for exactly
        // one poll can serve the genuine advisory.json alongside a signatures/index.json listing
        // only one of two real co-signers: every LISTED file still fetches successfully, so
        // SignaturesComplete is true, but quorum fails. Caching the hash here would let
        // AdvisoryFetcher's own dedup skip this advisory's directory FOREVER, even after the mirror
        // goes back to listing both signatures - because ContentHash never changes and signatures
        // live in sibling files the fetcher would then never request again.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: true, a.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Unverified, entry.Status);
        Assert.Equal("", entry.ContentHash);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task NeedsDecision_advisory_caches_its_content_hash()
    {
        // NeedsDecision (a computed action held open for an admin by manual mode, a notify-only
        // pin, or the severity gate) is final "on content" - an admin resolves it through the
        // ledger itself, not by the advisory's bytes changing - so it belongs in the terminal/cache
        // set alongside Acted, Suppressed, and NotApplicable.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        settings.AutoApply = false; // Manual mode -> SecSwitchAction.Notify -> NeedsDecision
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NeedsDecision, entry.Status);
        Assert.Equal("hash-a1", entry.ContentHash);
        Assert.Empty(sink.Calls); // Notify never touches the instance (ActionExecutor's own contract)
    }

    [Fact]
    public async Task Adversarial_partial_signature_list_does_not_permanently_disarm_the_advisory()
    {
        // End-to-end shape from Task 11 review Finding R1: sweep 1 is COMPLETE (every signature
        // file the hostile-for-one-poll mirror LISTED was fetched) but lists only one of the two
        // real signatures, so quorum fails and the advisory is recorded Unverified with its
        // ContentHash withheld. Sweep 2 - the mirror now honest, listing both signatures - proves
        // the withheld hash actually mattered: the SAME advisory id reaches the monitor again and
        // quorum is met.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);

        var firstSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: true, a.SignDetached(payload))],
            State(), settings, CancellationToken.None);
        var firstEntry = Assert.Single(firstSweep);
        Assert.Equal(LedgerStatus.Unverified, firstEntry.Status);
        Assert.Equal("", firstEntry.ContentHash);
        Assert.Empty(sink.Calls);

        var secondSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var secondEntry = Assert.Single(secondSweep);
        Assert.Equal(LedgerStatus.Acted, secondEntry.Status);
        Assert.Equal("hash-a1", secondEntry.ContentHash);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
        Assert.True(await ledger.IsActedAsync("a1"));
    }

    // --- Hard requirement 2: AdvisoryApplicability.IsApplicable returns false with a reason
    // containing "could not be determined" when it resolved an identifier but could not establish
    // the installed version. PolicyResolver folds that into the same SecSwitchAction.None as a
    // genuine not-applicable result - the monitor must not let the two collapse into the same
    // ledger status, or an indeterminate case silently reads as "nothing to worry about" to an
    // admin instead of "we don't actually know".

    [Fact]
    public async Task Indeterminate_applicability_is_recorded_as_needing_attention()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);
        var indeterminateState = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = null! }, Version.Parse("2.4.2"), CanUseSsh: false);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            indeterminateState, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.NotEqual(LedgerStatus.NotApplicable, entry.Status);
        // NeedsAttention is final only because we could not tell, never because content settled
        // anything - must not be cached (Finding R1).
        Assert.Equal("", entry.ContentHash);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Genuinely_not_applicable_advisory_is_recorded_distinctly_from_indeterminate()
    {
        // Control case proving the two statuses are actually distinguished, not that everything
        // funnels into "NeedsAttention" regardless of cause.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);
        var stateWithoutPlugin = new InstanceState(
            new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: false);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            stateWithoutPlugin, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NotApplicable, entry.Status);
        // Finding R1: NotApplicable IS cached - nothing about re-fetching identical bytes could
        // change a genuine not-applicable verdict.
        Assert.Equal("hash-a1", entry.ContentHash);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Null_instance_state_is_recorded_as_needing_attention()
    {
        // Task 11 review, Finding I3's "use your judgement" note: PolicyResolver also maps a null
        // InstanceState to SecSwitchAction.None ("Instance state is null."), a third distinct
        // reason - alongside the indeterminate-version case - for which "NotApplicable" would
        // falsely claim "you are not affected" rather than "we don't actually know". Chose to group
        // it with NeedsAttention rather than invent a fourth status, since the admin-facing meaning
        // ("we could not tell") is the same.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            null!, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.Empty(sink.Calls);
    }

    // --- Task 13 review, Finding I2 (Important) fix, end to end: a fixable core advisory reaching
    // this monitor while SSH is configured but CheckConfigurationHostedService has not yet reported
    // success must be retried next poll (NeedsAttention), never cached as the terminal
    // Acted/ShutdownCore - see InstanceState.SshVerificationPending's own doc comment for why getting
    // this wrong would be effectively permanent (Acted is terminal; no later poll could ever correct
    // it back to the real UpdateCore).

    [Fact]
    public async Task Core_advisory_with_ssh_verification_pending_is_recorded_as_needing_attention()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var coreAdvisoryJson = """
        {"id":"core1","identifier":"BTCPayServer","affectedVersions":"<2.5.0","fixedVersion":"2.5.0",
         "severity":"critical","title":"Core bug","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(coreAdvisoryJson);
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        var pendingState = new InstanceState(
            new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: false, SshVerificationPending: true);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("core1", "hash-core1", payload,
                [a.SignDetached(payload), b.SignDetached(payload)], true)],
            pendingState, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.Empty(sink.Calls); // nothing stopped, nothing triggered over SSH
        // NeedsAttention is never cached (Finding R1) - must remain eligible for re-evaluation.
        Assert.Equal("", entry.ContentHash);
        Assert.False(await ledger.IsActedAsync("core1"));
    }

    [Fact]
    public async Task Core_advisory_with_ssh_genuinely_not_configured_still_shuts_down()
    {
        // Regression guard: SshVerificationPending false (the default every pre-existing State()
        // helper call in this file already uses) must still take the original ShutdownCore path.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var coreAdvisoryJson = """
        {"id":"core1","identifier":"BTCPayServer","affectedVersions":"<2.5.0","fixedVersion":"2.5.0",
         "severity":"critical","title":"Core bug","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(coreAdvisoryJson);
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        var noSshState = new InstanceState(new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: false);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("core1", "hash-core1", payload,
                [a.SignDetached(payload), b.SignDetached(payload)], true)],
            noSshState, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.Contains("stop", sink.Calls);
        Assert.True(await ledger.IsActedAsync("core1"));
    }

    [Fact]
    public async Task Core_advisory_is_updated_once_ssh_verification_finishes_on_a_later_sweep()
    {
        // Task 13 review round 2, Finding R1: the two facts above (NeedsAttention is non-terminal, and
        // its ContentHash is withheld) are what make the retry described in PolicyResolver's own
        // deferral comment actually possible - proven here end to end, not just by composition, by
        // driving ProcessAsync twice for the SAME advisory: first while SSH verification is still
        // pending (deferred), then again once it has finished (acted). Mirrors the shape of
        // Unsuppressed_advisory_is_re_evaluated_by_a_later_sweep and
        // Advisory_latched_as_non_terminal_is_still_acted_on_in_a_later_sweep above, for this new gate.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var coreAdvisoryJson = """
        {"id":"core1","identifier":"BTCPayServer","affectedVersions":"<2.5.0","fixedVersion":"2.5.0",
         "severity":"critical","title":"Core bug","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(coreAdvisoryJson);
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        var fetched = new FetchedAdvisory("core1", "hash-core1", payload,
            [a.SignDetached(payload), b.SignDetached(payload)], true);

        var pendingState = new InstanceState(
            new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: false, SshVerificationPending: true);
        var firstSweep = await monitor.ProcessAsync([fetched], pendingState, settings, CancellationToken.None);

        Assert.Equal(LedgerStatus.NeedsAttention, Assert.Single(firstSweep).Status);
        Assert.Empty(sink.Calls);
        Assert.False(await ledger.IsActedAsync("core1"));

        var verifiedState = new InstanceState(new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: true);
        var secondSweep = await monitor.ProcessAsync([fetched], verifiedState, settings, CancellationToken.None);

        var finalEntry = Assert.Single(secondSweep);
        Assert.Equal(LedgerStatus.Acted, finalEntry.Status);
        Assert.Contains("core-update", sink.Calls);
        Assert.DoesNotContain("stop", sink.Calls); // UpdateCore never also stops - see ActionExecutor's own comment
        Assert.True(await ledger.IsActedAsync("core1"));
    }

    // --- Finding I3: Enabled has no initializer (defaults to false), and PolicyResolver's
    // "SecSwitch is disabled" None-reason carries no distinguishing phrase, so without a
    // short-circuit every advisory in the feed would be recorded as "NotApplicable" - a status
    // that affirmatively (and falsely) tells the admin "you are not affected". ProcessAsync must
    // record NOTHING at all while disabled or unconfigured.

    [Fact]
    public async Task Disabled_settings_records_nothing_at_all()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        settings.Enabled = false;
        var (monitor, sink, ledger) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Empty(entries);
        Assert.Empty(sink.Calls);
        Assert.False(await ledger.IsHandledAsync("a1"));
    }

    [Fact]
    public async Task Null_settings_records_nothing_at_all()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var (monitor, sink, ledger) = Make(Settings(a, b));

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), null!, CancellationToken.None);

        Assert.Empty(entries);
        Assert.Empty(sink.Calls);
        Assert.False(await ledger.IsHandledAsync("a1"));
    }

    // --- Finding I2: advisory-derived text must be sanitized before it reaches a LedgerEntry -
    // Title/Identifier/Severity are assigned (and, for the Unverified branch, persisted) BEFORE
    // AdvisoryVerifier.Verify runs, so an UNVERIFIED, attacker-forged advisory.json can still
    // reach the ledger. ActionExecutor.Sanitize truncates at 200 characters and strips control
    // characters plus U+2028/U+2029 specifically (char.IsControl alone does not catch those two -
    // they are Unicode category Zl/Zp, not Cc). Plain spaces are NOT stripped by Sanitize, so these
    // assertions check length/exact-value rather than "does not contain a space".

    [Fact]
    public async Task Line_separator_in_title_is_stripped_before_reaching_an_unverified_ledger_entry()
    {
        //  below is a literal JSON unicode-escape SEQUENCE (backslash, u, 2, 0, 2, 8) - a raw
        // string literal never interprets \ specially, so JsonDocument is the one decoding it into
        // an actual U+2028 character in advisory.Title, exactly as a real attacker-supplied
        // advisory.json would deliver one.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var json = """
        {"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <2.0.0","fixedVersion":"2.0.0",
         "severity":"critical","title":"Bad\u2028Title","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(json);
        var settings = Settings(a, b); // only ONE of the two keys signs -> quorum not met, Unverified
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", payload, [a.SignDetached(payload)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Unverified, entry.Status);
        Assert.Equal("BadTitle", entry.Title); // the separator is gone, not merely hidden by truncation
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Oversized_title_is_truncated_before_reaching_the_ledger()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var hostileTitle = new string('x', 5000);
        var json = $$"""
        {"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <2.0.0","fixedVersion":"2.0.0",
         "severity":"critical","title":"{{hostileTitle}}","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(json);
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", payload, [a.SignDetached(payload)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(203, entry.Title.Length); // Sanitize's 200-char cap plus "..."
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Oversized_identifier_is_truncated_before_reaching_the_ledger()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var hostileIdentifier = "Plug" + new string('y', 5000);
        var json = $$"""
        {"id":"a1","identifier":"{{hostileIdentifier}}","affectedVersions":">=1.0.0 && <2.0.0",
         "fixedVersion":"2.0.0","severity":"critical","title":"t","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(json);
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", payload, [a.SignDetached(payload)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(203, entry.Identifier.Length);
    }

    [Fact]
    public async Task Parse_error_text_embedding_attacker_content_is_sanitized_before_reaching_the_ledger_reason()
    {
        // Final whole-branch review, Finding M9: this test used to feed `"{ " + new string('"', 5000)`
        // and assert only Reason.Length <= 203. That was vacuous - System.Text.Json's reader
        // exception reports a byte position and never echoes the payload, so its message is short and
        // control-character-free either way, and the assertion passed identically with the Sanitize
        // call deleted. AdvisoryParser DOES have a parse-error path that embeds attacker bytes
        // verbatim: `Unrecognised severity '{severityRaw}'`, where severityRaw is an unbounded string
        // straight out of the (still unverified) advisory. Feeding an oversized, control-character-
        // laden severity makes both halves of Sanitize load-bearing - remove the call and Reason is
        // ~5,000 characters long and carries a raw control character into the persisted ledger row and
        // the admin audit page.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        // The control character goes FIRST, not last, on purpose: appended after 5,000 filler
        // characters it would be cut off by the length cap alone, and the strip half of Sanitize
        // would never be exercised. Written as the JSON escape  rather than a literal control
        // character in this source file, matching ActionExecutor's own reasoning about U+2028/U+2029.
        var hostileSeverity = "\\u0007" + new string('s', 5000);
        var json = $$"""
        {"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <2.0.0","fixedVersion":"2.0.0",
         "severity":"{{hostileSeverity}}","title":"t","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var junk = Encoding.UTF8.GetBytes(json);
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", junk, [a.SignDetached(junk), b.SignDetached(junk)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Rejected, entry.Status);
        Assert.Contains("Unrecognised severity", entry.Reason); // the error path that embeds the payload
        Assert.True(entry.Reason.Length <= 203, $"Reason was {entry.Reason.Length} characters; Sanitize caps it at 200 plus an ellipsis.");
        Assert.DoesNotContain("", entry.Reason, StringComparison.Ordinal); // stripped, not merely truncated away
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Hostile_text_in_a_policy_reason_is_sanitized_before_reaching_the_ledger()
    {
        // decision.Reason (from PolicyResolver, via AdvisoryApplicability) can itself embed
        // attacker-controlled text - here, an oversized Identifier flows into the
        // "{identifier} is not installed on this instance." reason for a genuinely-not-applicable
        // advisory.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var hostileIdentifier = new string('z', 5000);
        var json = $$"""
        {"id":"a1","identifier":"{{hostileIdentifier}}","affectedVersions":">=1.0.0 && <2.0.0",
         "fixedVersion":"2.0.0","severity":"critical","title":"t","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(json);
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", payload, [a.SignDetached(payload), b.SignDetached(payload)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NotApplicable, entry.Status);
        Assert.True(entry.Reason.Length <= 203);
        Assert.Empty(sink.Calls);
    }

    // --- Design intent: ProcessAsync must never throw - one hostile/malformed entry in a batch
    // must not abort processing of every other advisory in the same sweep.

    [Fact]
    public async Task Empty_batch_processes_cleanly()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync([], State(), settings, CancellationToken.None);

        Assert.Empty(entries);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Malformed_batch_entry_does_not_abort_the_sweep()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);
        var bad = new FetchedAdvisory("bad", "hash-bad", null!, [], true);
        var good = new FetchedAdvisory(
            "a1", "hash-a1", payload, [a.SignDetached(payload), b.SignDetached(payload)], true);

        var entries = await monitor.ProcessAsync([bad, good], State(), settings, CancellationToken.None);

        Assert.Contains(entries, e => e.AdvisoryId == "a1" && e.Status == LedgerStatus.Acted);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
    }

    [Fact]
    public async Task Null_batch_does_not_throw()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(null!, State(), settings, CancellationToken.None);

        Assert.Empty(entries);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Duplicate_trusted_key_fingerprints_in_settings_do_not_abort_the_sweep()
    {
        // settings.TrustedKeys is admin-supplied config, not attacker-controlled advisory content,
        // but a naive ToDictionary over it throws ArgumentException on a duplicate fingerprint -
        // which would abort the WHOLE sweep (every advisory in the batch), not just one entry.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        settings.TrustedKeys.Add(new TrustedKey
        { Fingerprint = a.Fingerprint, ArmoredPublicKey = a.ArmoredPublicKey, Identity = "dup" });
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Contains("update:Plug:2.0.0", sink.Calls);
        Assert.Equal(LedgerStatus.Acted, Assert.Single(entries).Status);
    }

    [Fact]
    public async Task Null_element_in_armored_signatures_does_not_abort_the_sweep()
    {
        // AdvisoryVerifier.ParseSignatures already guards a null armored-signature entry (Task 4) -
        // this proves that guarantee holds end to end through the monitor, and that the two genuine
        // signatures alongside it still meet quorum (a null entry is inert, not poisoning).
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);
        var withNullSignature = new FetchedAdvisory(
            "a1", "hash-a1", payload, [null!, a.SignDetached(payload), b.SignDetached(payload)], true);

        var entries = await monitor.ProcessAsync(
            [withNullSignature], State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
    }

    // --- Finding M7: LedgerStore and ActionExecutor were both independently hardened (Tasks 7 and
    // 9) to never throw for any input they accept - so neither can be made to throw from OUTSIDE
    // via a hostile argument the way AdvisoryParser/AdvisoryVerifier/PolicyResolver can. What CAN
    // be exercised is the per-item backstop catching an unexpected failure of the DEPENDENCY
    // itself: a broken ISettingsRepository underneath LedgerStore (both a read-time and a
    // write-time failure, since they are reached at different points in ProcessOneAsync), and -
    // since the real, sealed ActionExecutor structurally cannot throw (every branch lives inside
    // one try/catch; confirmed by re-reading ActionExecutor.ExecuteAsync) - a missing (null)
    // executor as the closest honest proxy for "the dependency SecSwitchMonitor calls at that
    // point throws", since it fails at the exact same call site with the exact same shape
    // (an exception escaping executor.ExecuteAsync(...)) a genuinely throwing one would.

    sealed class ThrowingSettingsRepository : ISettingsRepository
    {
        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class =>
            throw new InvalidOperationException("boom");
        public Task UpdateSetting<T>(T obj, string? name = null) where T : class =>
            throw new InvalidOperationException("boom");
        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class =>
            throw new NotSupportedException();
    }

    sealed class ThrowingOnWriteSettingsRepository : ISettingsRepository
    {
        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class => Task.FromResult<T?>(null);
        public Task UpdateSetting<T>(T obj, string? name = null) where T : class =>
            throw new InvalidOperationException("boom");
        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Ledger_read_failure_does_not_abort_the_sweep()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var sink = new RecordingSink();
        var brokenLedger = new LedgerStore(new ThrowingSettingsRepository());
        var monitor = new SecSwitchMonitor(
            new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance),
            brokenLedger, NullLogger<SecSwitchMonitor>.Instance);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None); // must not throw

        Assert.Empty(entries); // IsActedAsync itself threw, before anything could be evaluated
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Ledger_write_failure_after_a_real_action_does_not_abort_the_sweep()
    {
        // The worst-case ordering: the action executor genuinely runs (queues an update and stops
        // the app) BEFORE the ledger write that then fails, so the outcome is real but never
        // durably recorded - the per-item backstop must still swallow it rather than propagate.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var sink = new RecordingSink();
        var brokenLedger = new LedgerStore(new ThrowingOnWriteSettingsRepository());
        var monitor = new SecSwitchMonitor(
            new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance),
            brokenLedger, NullLogger<SecSwitchMonitor>.Instance);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None); // must not throw

        Assert.Contains("update:Plug:2.0.0", sink.Calls); // the action really was taken
        Assert.Empty(entries); // but the write that would have recorded it failed and was swallowed
    }

    [Fact]
    public async Task Null_executor_dependency_does_not_abort_the_sweep()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var monitor = new SecSwitchMonitor(null!, ledger, NullLogger<SecSwitchMonitor>.Instance);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None); // must not throw

        Assert.Empty(entries);
    }

    // ================================================================================
    // Final whole-branch review, Finding C1 (Critical): a FAILED action must never be recorded as
    // the terminal `Acted`.
    //
    // ActionExecutor returned one string for success and failure alike and this class ignored it,
    // so "Failed to queue disable of X", "Refusing to update: ... is missing or unsafe", and
    // "Action UpdateCore failed: SSH is not configured" all landed under `Acted`: terminal, so
    // IsActedAsync latched the advisory out of every future poll AND the content hash was cached so
    // the fetcher never re-downloaded it, while the bell said "Handled" and the audit page showed a
    // green "Acted - SecSwitch applied the resulting action automatically". Reachable with no
    // attacker at all - a core-bundled plugin has no directory under PluginDir for
    // BtcPayActionSink.TryResolveInstalledDirectory to resolve, so an advisory against one is
    // applicable, resolves to DisablePlugin, queues nothing, and was filed as handled forever.
    //
    // Each test below asserts the full set of consequences, not just the status: NeedsAttention,
    // an EMPTY ContentHash (so AdvisoryFetcher re-delivers it), IsActedAsync false (so this class
    // will look again), and a later sweep that actually does re-evaluate and act.
    // ================================================================================

    /// A sink that resolves nothing and queues nothing - what BtcPayActionSink genuinely does for an
    /// identifier with no matching installed plugin directory (a core-bundled/system plugin), or an
    /// ambiguous case-only match. Reports failure by return value, without throwing.
    sealed class UnresolvableSink : IActionSink
    {
        public List<string> Calls { get; } = [];
        public bool QueueDisable(string identifier) { Calls.Add($"disable-attempt:{identifier}"); return false; }
        public Task<bool> QueueUpdateAsync(string identifier, string version)
        { Calls.Add($"update-attempt:{identifier}:{version}"); return Task.FromResult(false); }
        public Task TriggerCoreUpdateAsync() { Calls.Add("core-update-attempt"); return Task.CompletedTask; }
        public void StopApplication() => Calls.Add("stop");
    }

    /// A sink whose core-update path throws the way BtcPayActionSink.TriggerCoreUpdateAsync really
    /// does when SSH is not configured, or when ConnectAsync cannot reach the host.
    sealed class SshFailingSink : IActionSink
    {
        public List<string> Calls { get; } = [];
        public bool QueueDisable(string identifier) { Calls.Add($"disable:{identifier}"); return true; }
        public Task<bool> QueueUpdateAsync(string identifier, string version)
        { Calls.Add($"update:{identifier}:{version}"); return Task.FromResult(true); }
        public Task TriggerCoreUpdateAsync() =>
            throw new InvalidOperationException("SSH is not configured; cannot trigger a core update.");
        public void StopApplication() => Calls.Add("stop");
    }

    static SecSwitchMonitor MonitorWith(IActionSink sink, LedgerStore ledger) =>
        new(new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance),
            ledger, NullLogger<SecSwitchMonitor>.Instance);

    // An advisory with NO fixedVersion, so PolicyResolver resolves DisablePlugin rather than
    // UpdatePlugin regardless of PreferUpdateOverDisable.
    static readonly string NoFixAdvisoryJson = """
    {"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <2.0.0",
     "severity":"critical","title":"Bad","description":"d","references":[],
     "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
    """;

    // A CORE advisory with a fixedVersion, so PolicyResolver resolves UpdateCore when SSH is usable.
    static readonly string CoreFixableAdvisoryJson = """
    {"id":"core1","identifier":"BTCPayServer","affectedVersions":"<2.5.0","fixedVersion":"2.5.0",
     "severity":"critical","title":"Core bug","description":"d","references":[],
     "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
    """;

    [Fact]
    public async Task Failed_disable_is_recorded_as_needing_attention_and_re_evaluated_later()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Encoding.UTF8.GetBytes(NoFixAdvisoryJson);
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var failing = new UnresolvableSink();
        var fetched = new FetchedAdvisory(
            "a1", "hash-a1", payload, [a.SignDetached(payload), b.SignDetached(payload)], true);

        var entries = await MonitorWith(failing, ledger)
            .ProcessAsync([fetched], State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("DisablePlugin", entry.Action); // the action really was resolved and attempted
        Assert.Contains("disable-attempt:Plug", failing.Calls);
        Assert.DoesNotContain("stop", failing.Calls); // never stop on the strength of a queue that did not happen
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.NotEqual(LedgerStatus.Acted, entry.Status);
        Assert.Equal("", entry.ContentHash); // withheld, so AdvisoryFetcher re-delivers it next poll
        Assert.False(await ledger.IsActedAsync("a1")); // not latched out of future polls
        Assert.Contains("Failed to queue disable", entry.Reason);

        // The whole point of the non-terminal status: a later sweep must genuinely re-evaluate, and
        // act, once the underlying cause is gone.
        var working = new RecordingSink();
        var second = await MonitorWith(working, ledger)
            .ProcessAsync([fetched], State(), settings, CancellationToken.None);

        Assert.Equal(LedgerStatus.Acted, Assert.Single(second).Status);
        Assert.Equal(["disable:Plug", "stop"], working.Calls);
        Assert.True(await ledger.IsActedAsync("a1"));
    }

    [Fact]
    public async Task Failed_update_is_recorded_as_needing_attention_and_re_evaluated_later()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload; // has fixedVersion 2.0.0 -> UpdatePlugin
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var failing = new UnresolvableSink();
        var fetched = new FetchedAdvisory(
            "a1", "hash-a1", payload, [a.SignDetached(payload), b.SignDetached(payload)], true);

        var entries = await MonitorWith(failing, ledger)
            .ProcessAsync([fetched], State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("UpdatePlugin", entry.Action);
        Assert.Contains("update-attempt:Plug:2.0.0", failing.Calls);
        Assert.DoesNotContain("stop", failing.Calls);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.Equal("", entry.ContentHash);
        Assert.False(await ledger.IsActedAsync("a1"));
        Assert.Contains("Failed to queue update", entry.Reason);

        var working = new RecordingSink();
        var second = await MonitorWith(working, ledger)
            .ProcessAsync([fetched], State(), settings, CancellationToken.None);

        Assert.Equal(LedgerStatus.Acted, Assert.Single(second).Status);
        Assert.Contains("update:Plug:2.0.0", working.Calls);
        Assert.True(await ledger.IsActedAsync("a1"));
    }

    [Fact]
    public async Task Failed_core_update_is_recorded_as_needing_attention_and_re_evaluated_later()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Encoding.UTF8.GetBytes(CoreFixableAdvisoryJson);
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var failing = new SshFailingSink();
        // CanUseSsh true -> UpdateCore. The sink then fails to connect, exactly as a rotated key or
        // an unreachable host would at runtime.
        var sshState = new InstanceState(
            new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: true);
        var fetched = new FetchedAdvisory(
            "core1", "hash-core1", payload, [a.SignDetached(payload), b.SignDetached(payload)], true);

        var entries = await MonitorWith(failing, ledger)
            .ProcessAsync([fetched], sshState, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("UpdateCore", entry.Action);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.NotEqual(LedgerStatus.Acted, entry.Status);
        Assert.Equal("", entry.ContentHash);
        Assert.False(await ledger.IsActedAsync("core1"));
        Assert.Contains("failed", entry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stop", failing.Calls); // a failed core update must not stop the server either

        var working = new RecordingSink();
        var second = await MonitorWith(working, ledger)
            .ProcessAsync([fetched], sshState, settings, CancellationToken.None);

        Assert.Equal(LedgerStatus.Acted, Assert.Single(second).Status);
        Assert.Contains("core-update", working.Calls);
        Assert.True(await ledger.IsActedAsync("core1"));
    }

    [Fact]
    public async Task Refused_unsafe_identifier_is_recorded_as_needing_attention_not_acted()
    {
        // ActionExecutor refuses a traversal-shaped identifier before any sink call at all. That is
        // still a resolved-but-unapplied action, so it belongs in the same non-terminal bucket -
        // otherwise a hostile identifier is the cheapest possible way to get an advisory permanently
        // filed as handled.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var json = """
        {"id":"a1","identifier":"../evil","affectedVersions":">=1.0.0 && <2.0.0","fixedVersion":"2.0.0",
         "severity":"critical","title":"Bad","description":"d","references":[],
         "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
        """;
        var payload = Encoding.UTF8.GetBytes(json);
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var sink = new RecordingSink();
        var state = new InstanceState(
            new Dictionary<string, Version> { ["../evil"] = Version.Parse("1.5.0") },
            Version.Parse("2.4.2"), CanUseSsh: false);

        var entries = await MonitorWith(sink, ledger)
            .ProcessAsync(
                [new FetchedAdvisory("a1", "hash-a1", payload, [a.SignDetached(payload), b.SignDetached(payload)], true)],
                state, settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.Empty(sink.Calls);
        Assert.Equal("", entry.ContentHash);
        Assert.False(await ledger.IsActedAsync("a1"));
    }

    // ================================================================================
    // Final whole-branch review, Finding C2 (Critical): the ledger identity and the action latch must
    // come from the SIGNED advisory, never from the unsigned index.json.
    //
    // FetchedAdvisory.Id is parsed from index.json - the one artefact in the feed carrying no
    // signature - and it used to key the pre-parse IsActedAsync check AND the ledger row, while the
    // signed Advisory.Id was never compared against it. An attacker who can write index.json and
    // holds NO signing keys therefore got a silent drop and a crash loop for free. Both directions
    // are pinned below.
    // ================================================================================

    // A genuine, correctly-signed CORE advisory with no fix: resolves to ShutdownCore. Signed id
    // "core1".
    static readonly string CoreUnfixableAdvisoryJson = """
    {"id":"core1","identifier":"BTCPayServer","affectedVersions":"<2.5.0",
     "severity":"critical","title":"Unfixable core bug","description":"d","references":[],
     "publishedAt":"2026-08-25T00:00:00Z","revoked":false}
    """;

    [Fact]
    public async Task Forged_index_id_cannot_silently_drop_a_genuine_new_advisory()
    {
        // ATTACK 1 (silent drop): rewrite one index `id` so a genuine, correctly-signed NEW advisory
        // carries the id of something already terminal. Pre-fix, IsActedAsync(item.Id) fired before
        // parse and before verification, so the advisory vanished entirely - no ledger row, no log,
        // no notification, nothing for an admin to ever see.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload; // genuinely signed, and its OWN id is "a1"
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var sink = new RecordingSink();
        await ledger.RecordAsync(new LedgerEntry
        {
            AdvisoryId = "old-terminal", Status = LedgerStatus.Acted, ContentHash = "hash-old",
            Reason = "the original, genuine outcome", RecordedAt = DateTimeOffset.UtcNow
        });

        var entries = await MonitorWith(sink, ledger).ProcessAsync(
            [new FetchedAdvisory("old-terminal", "hash-forged", payload,
                [a.SignDetached(payload), b.SignDetached(payload)], true)],
            State(), settings, CancellationToken.None);

        // Not dropped: a row exists, and it is filed under the SIGNED id, not the index's.
        var entry = Assert.Single(entries);
        Assert.Equal("a1", entry.AdvisoryId);
        Assert.Equal(LedgerStatus.NeedsAttention, entry.Status);
        Assert.True(SecSwitchPeriodicTask.IsNotifiable(entry.Status)); // the admin is actually told
        Assert.Equal("", entry.ContentHash); // non-terminal -> re-fetched, never cached away
        Assert.False(await ledger.IsActedAsync("a1"));
        Assert.Empty(sink.Calls); // and nothing was acted on off the back of a lying index

        // The unrelated terminal row the forged id pointed at is untouched - a non-terminal write
        // over it would have un-latched it and handed the attacker a re-action loop.
        var state = await ledger.GetAsync();
        Assert.Equal(LedgerStatus.Acted, state.Entries["old-terminal"].Status);
        Assert.Equal("hash-old", state.Entries["old-terminal"].ContentHash);
        Assert.Equal("the original, genuine outcome", state.Entries["old-terminal"].Reason);
        Assert.True(await ledger.IsActedAsync("old-terminal"));
    }

    [Fact]
    public async Task Rotating_index_ids_cannot_re_run_a_shutdown_on_every_poll()
    {
        // ATTACK 2 (crash loop on a live payment server): rotate `id` AND `contentHash` on every
        // publish while `path` keeps pointing at a genuine, correctly-signed, unfixable-core
        // advisory. The fetcher's hash dedup never fires (the hash is always new) and the id-keyed
        // latch never fires (the id is always new), so ShutdownCore ran every single poll. This is
        // the exact scenario LedgerStore's own doc comment names as reason #1 the ledger exists.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Encoding.UTF8.GetBytes(CoreUnfixableAdvisoryJson); // signed id is "core1"
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var sink = new RecordingSink();
        var monitor = MonitorWith(sink, ledger);
        var noSshState = new InstanceState(
            new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: false);

        for (var poll = 0; poll < 5; poll++)
        {
            var entries = await monitor.ProcessAsync(
                [new FetchedAdvisory($"rotated-{poll}", $"hash-{poll}", payload,
                    [a.SignDetached(payload), b.SignDetached(payload)], true)],
                noSshState, settings, CancellationToken.None);

            Assert.Equal(LedgerStatus.NeedsAttention, Assert.Single(entries).Status);
        }

        Assert.Empty(sink.Calls); // five polls, zero shutdowns - the loop never starts
        // One ledger row across all five polls, keyed by the signed id: the attacker's rotating ids
        // cannot inflate the single settings row either.
        var state = await ledger.GetAsync();
        var onlyKey = Assert.Single(state.Entries.Keys);
        Assert.Equal("core1", onlyKey);
    }

    [Fact]
    public async Task Honest_index_id_still_acts_once_and_is_then_latched_by_the_signed_id()
    {
        // The control for the two attacks above: with a truthful index the advisory is acted on
        // exactly once, and a later poll with a DIFFERENT content hash (so the fetcher re-delivers
        // it) is turned away by the signed-id latch rather than re-running the shutdown.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Encoding.UTF8.GetBytes(CoreUnfixableAdvisoryJson);
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var sink = new RecordingSink();
        var monitor = MonitorWith(sink, ledger);
        var noSshState = new InstanceState(
            new Dictionary<string, Version>(), Version.Parse("2.4.2"), CanUseSsh: false);

        var first = await monitor.ProcessAsync(
            [new FetchedAdvisory("core1", "hash-1", payload,
                [a.SignDetached(payload), b.SignDetached(payload)], true)],
            noSshState, settings, CancellationToken.None);
        Assert.Equal(LedgerStatus.Acted, Assert.Single(first).Status);
        Assert.Equal(["stop"], sink.Calls);

        var second = await monitor.ProcessAsync(
            [new FetchedAdvisory("core1", "hash-2", payload,
                [a.SignDetached(payload), b.SignDetached(payload)], true)],
            noSshState, settings, CancellationToken.None);

        Assert.Empty(second);
        Assert.Equal(["stop"], sink.Calls); // still exactly one stop, not two
    }

    [Fact]
    public async Task Index_id_differing_only_by_case_is_accepted_as_a_match()
    {
        // Every other identifier comparison in this plugin is OrdinalIgnoreCase (TrustStore,
        // PolicyResolver, AdvisoryVerifier, AdvisoryApplicability, LedgerStore) - the id-match check
        // must be too, or an honest feed that merely differs in casing would be permanently refused.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload; // signed id "a1"
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var sink = new RecordingSink();

        var entries = await MonitorWith(sink, ledger).ProcessAsync(
            [new FetchedAdvisory("A1", "hash-a1", payload,
                [a.SignDetached(payload), b.SignDetached(payload)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
        Assert.Equal("a1", entry.AdvisoryId); // normalised to the signed document's own casing
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
    }

    [Fact]
    public async Task A_terminal_ledger_record_is_never_downgraded_by_a_later_parse_failure()
    {
        // The protection the pre-parse latch used to provide implicitly, now stated explicitly (see
        // SecSwitchMonitor.RecordAsync). With the latch moved after verification, a parse or quorum
        // failure is REACHED for an id that is already terminal - and overwriting an Acted
        // ShutdownCore row with a non-terminal "Rejected" would clear both halves of the action-loop
        // guard at once (IsActedAsync goes false, and the content hash is withheld so the advisory is
        // re-delivered), turning one poll of malformed bytes from a hostile mirror into a repeatable
        // stop-the-server loop.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var junk = Encoding.UTF8.GetBytes("{ not json");
        var settings = Settings(a, b);
        var ledger = new LedgerStore(new FakeSettingsRepository());
        var sink = new RecordingSink();
        await ledger.RecordAsync(new LedgerEntry
        {
            AdvisoryId = "a1", Status = LedgerStatus.Acted, ContentHash = "hash-a1",
            Reason = "Stopped BTCPay Server due to an unfixable core advisory.",
            RecordedAt = DateTimeOffset.UtcNow
        });

        var entries = await MonitorWith(sink, ledger).ProcessAsync(
            [new FetchedAdvisory("a1", "hash-forged", junk, [a.SignDetached(junk), b.SignDetached(junk)], true)],
            State(), settings, CancellationToken.None);

        Assert.Empty(entries); // nothing recorded - the existing final record stands
        var state = await ledger.GetAsync();
        Assert.Equal(LedgerStatus.Acted, state.Entries["a1"].Status);
        Assert.Equal("hash-a1", state.Entries["a1"].ContentHash);
        Assert.True(await ledger.IsActedAsync("a1"));
        Assert.Empty(sink.Calls);
    }
}
