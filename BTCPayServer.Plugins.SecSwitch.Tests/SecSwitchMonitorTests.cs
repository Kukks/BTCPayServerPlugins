using System.Text;
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
        Assert.True(await ledger.IsHandledAsync("a1"));
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
        // a crash loop, not a kill switch. IsHandledAsync is the only thing preventing that.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var payload = Payload;
        var settings = Settings(a, b);
        var (monitor, sink, ledger) = Make(settings);
        await ledger.RecordAsync(new LedgerEntry { AdvisoryId = "a1", RecordedAt = DateTimeOffset.UtcNow });

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

    // --- IPeriodicTask surface: SecSwitchMonitor's constructor carries no advisory source,
    // settings store, or instance-state provider (see ProcessAsync's own parameters for what a
    // real poll needs) - wiring Do to actually fetch and call ProcessAsync is later-task work (see
    // task-11-report.md). Do must still be a safe, real IPeriodicTask implementation that never
    // throws, so registering this type before that wiring lands fails safe rather than crashing
    // the scheduler.

    [Fact]
    public async Task Do_completes_without_throwing_or_touching_the_sink()
    {
        var (monitor, sink, _) = Make(Settings());
        await monitor.Do(CancellationToken.None);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void SecSwitchMonitor_implements_IPeriodicTask()
    {
        var (monitor, _, _) = Make(Settings());
        Assert.IsAssignableFrom<BTCPayServer.HostedServices.IPeriodicTask>(monitor);
    }
}
