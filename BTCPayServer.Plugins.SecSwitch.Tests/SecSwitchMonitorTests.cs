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
        Assert.Equal("Acted", entry.Status);
        // SignaturesComplete was true, so the content hash IS persisted - see the incomplete-set
        // test below for the converse.
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
        Assert.Equal("Unverified", Assert.Single(entries).Status);
    }

    [Fact]
    public async Task Already_handled_advisory_is_not_acted_on_again()
    {
        // Hard requirement 3 / without this, a shutdown advisory would re-fire on every restart -
        // a crash loop, not a kill switch. IsActedAsync is the only thing preventing that.
        //
        // Fixture note (Task 11 review, Finding C1 fix): the pre-recorded entry now carries
        // Status="Acted" rather than a blank status. Under the pre-fix code (gated on
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
        { AdvisoryId = "a1", Status = "Acted", RecordedAt = DateTimeOffset.UtcNow });

        await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Suppressed_advisory_is_not_acted_on()
    {
        // Task 11 review, Finding C1 fix requirement: "Keep SuppressAsync latching - the admin's
        // escape hatch must stay absolute." Suppressed is one of IsActedAsync's three terminal
        // statuses, so this must hold even under the narrowed gate.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        await ledger.SuppressAsync("a1");

        await monitor.ProcessAsync(
            [Fetched("hash-a1", true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Advisory_latched_as_non_terminal_is_still_acted_on_in_a_later_sweep()
    {
        // Reproduces Task 11 review Finding C1 (Critical) end to end: hard requirement 1 withholds
        // ContentHash for an incomplete signature set specifically so AdvisoryFetcher re-downloads
        // it next poll. If THIS method then treated any recorded entry (Rejected, Unverified,
        // NotApplicable, NeedsAttention) as "already handled", that re-fetch would arrive here and
        // be turned away anyway - silently, permanently disarming SecSwitch for this advisory id,
        // with no key material needed: a hostile mirror serving malformed bytes, or simply
        // stripping signature files, for a single poll would be enough. Two sweeps for the SAME
        // advisory id: first below quorum (non-terminal), then quorum-met - the second sweep must
        // actually act.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);

        var firstSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: false, a.SignDetached(payload))],
            State(), settings, CancellationToken.None);
        Assert.Equal("Unverified", Assert.Single(firstSweep).Status);
        Assert.Empty(sink.Calls);

        var secondSweep = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: true, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(secondSweep);
        Assert.Equal("Acted", entry.Status);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
        Assert.True(await ledger.IsActedAsync("a1"));
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Unverified")]
    [InlineData("NotApplicable")]
    [InlineData("NeedsAttention")]
    [InlineData("")]
    public async Task Non_terminal_prior_status_does_not_block_a_later_sweep(string priorStatus)
    {
        // Direct, per-status companion to Advisory_latched_as_non_terminal_is_still_acted_on_in_a_
        // later_sweep above: every non-terminal status this monitor can itself produce (plus a
        // blank one, for a pre-existing/foreign entry) must leave the advisory eligible for
        // re-evaluation - not just the specific Unverified case that arises naturally from an
        // incomplete-then-complete fetch pair.
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
        Assert.Equal("Rejected", Assert.Single(entries).Status);
    }

    // --- Hard requirement 1: an advisory whose signature set was truncated by the fetcher's own
    // request budget or poll deadline must never have its content hash persisted. Persisting it
    // would mark the advisory "already seen" by AdvisoryFetcher's own dedup (it is fed the set of
    // recorded content hashes on the next poll), so a signature set that can never grow can never
    // reach quorum - a silent, permanent kill-switch failure for that advisory.

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
        // content hash as "seen" is wrong regardless of today's outcome.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: false, a.SignDetached(payload), b.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("Acted", entry.Status); // quorum WAS met - proves the gate is not just "unverified => no hash"
        Assert.Equal("", entry.ContentHash);
        Assert.Contains("update:Plug:2.0.0", sink.Calls);
    }

    [Fact]
    public async Task Complete_signature_set_persists_content_hash_regardless_of_outcome()
    {
        // Converse of the above: a COMPLETE fetch that still fails quorum is safe to mark seen -
        // the fetcher gathered everything the feed currently lists for this advisory, so nothing
        // is lost by not re-fetching identical bytes next poll.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [Fetched("hash-a1", signaturesComplete: true, a.SignDetached(payload))],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("Unverified", entry.Status);
        Assert.Equal("hash-a1", entry.ContentHash);
        Assert.Empty(sink.Calls);
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
        Assert.Equal("NeedsAttention", entry.Status);
        Assert.NotEqual("NotApplicable", entry.Status);
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
        Assert.Equal("NotApplicable", entry.Status);
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
        Assert.Equal("NeedsAttention", entry.Status);
        Assert.Empty(sink.Calls);
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
        Assert.Equal("Unverified", entry.Status);
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
    public async Task Malformed_json_error_text_is_sanitized_before_reaching_the_ledger_reason()
    {
        // parseError can embed a raw JsonException.Message; sanitized the same way as every other
        // attacker-influenced string reaching the ledger.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var junk = Encoding.UTF8.GetBytes("{ " + new string('"', 5000));
        var settings = Settings(a, b);
        var (monitor, sink, _) = Make(settings);

        var entries = await monitor.ProcessAsync(
            [new FetchedAdvisory("a1", "hash-a1", junk, [a.SignDetached(junk), b.SignDetached(junk)], true)],
            State(), settings, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("Rejected", entry.Status);
        Assert.True(entry.Reason.Length <= 203);
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
        Assert.Equal("NotApplicable", entry.Status);
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

        Assert.Contains(entries, e => e.AdvisoryId == "a1" && e.Status == "Acted");
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
        Assert.Equal("Acted", Assert.Single(entries).Status);
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
        Assert.Equal("Acted", entry.Status);
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
}
