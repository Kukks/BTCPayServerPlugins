using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;
using NewtonsoftJsonConvert = Newtonsoft.Json.JsonConvert;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// In-memory ISettingsRepository so ledger logic is testable without a database.
public sealed class FakeSettingsRepository : ISettingsRepository
{
    readonly Dictionary<string, object> _store = new();

    public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        => Task.FromResult(_store.TryGetValue(name ?? typeof(T).FullName!, out var v) ? (T?)v : null);

    public Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
        _store[name ?? typeof(T).FullName!] = obj;
        return Task.CompletedTask;
    }

    public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        => throw new NotSupportedException();
}

/// Wraps a private JSON-string store and round-trips every value through JSON serialization on
/// both read and write - the same way the REAL SettingsRepository (BTCPayServer core,
/// Services/SettingsRepository.cs) behaves: GetSettingAsync deserializes a fresh object graph
/// from a cached JSON string on every call, and never hands back the object instance that was
/// last passed to UpdateSetting. FakeSettingsRepository above instead stores and returns the SAME
/// object reference every time, which could hide a LedgerStore bug that mutates a ledger object
/// and relies on that in-place mutation alone being visible, without an explicit UpdateSetting
/// call ever actually reaching the repository. Tests using this repository instead prove
/// LedgerStore has no such dependency on aliasing.
public sealed class JsonRoundTrippingSettingsRepository : ISettingsRepository
{
    readonly Dictionary<string, string> _store = new();

    public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
    {
        var key = name ?? typeof(T).FullName!;
        return Task.FromResult(_store.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : null);
    }

    public Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
        _store[name ?? typeof(T).FullName!] = JsonSerializer.Serialize(obj);
        return Task.CompletedTask;
    }

    public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        => throw new NotSupportedException();
}

/// Wraps another ISettingsRepository and inserts a real async yield around each operation, so
/// that concurrent LedgerStore read-modify-write sequences genuinely interleave under test. Every
/// FakeSettingsRepository operation completes synchronously (Task.FromResult / Task.CompletedTask
/// - no real I/O), so without a genuine suspension point here an unsynchronized LedgerStore call
/// tends to run start-to-finish on one thread before the next call's continuation is scheduled,
/// making a lost-update race unreliable to reproduce in a fast unit test. This widens the race
/// window enough to make a lost update near-certain against an unsynchronized implementation.
public sealed class SlowSettingsRepository(ISettingsRepository inner) : ISettingsRepository
{
    public async Task<T?> GetSettingAsync<T>(string? name = null) where T : class
    {
        await Task.Yield();
        return await inner.GetSettingAsync<T>(name);
    }

    public async Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
        await Task.Yield();
        await inner.UpdateSetting(obj, name);
    }

    public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        => inner.WaitSettingsChanged<T>(cancellationToken);
}

public class LedgerStoreTests
{
    static LedgerEntry Entry(string id, string action = "UpdatePlugin") => new()
    {
        AdvisoryId = id, Title = "t", Identifier = "Plug", Severity = "High",
        Status = LedgerStatus.Acted, Action = action, Reason = "r", RecordedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Empty_ledger_reports_nothing_handled()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.IsHandledAsync("nope"));
        Assert.Empty((await store.GetAsync()).Entries);
    }

    [Fact]
    public async Task Recorded_advisory_is_reported_handled()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        Assert.True(await store.IsHandledAsync("a1"));
        Assert.False(await store.IsHandledAsync("a2"));
    }

    [Fact]
    public async Task Recording_the_same_advisory_twice_does_not_duplicate()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        await store.RecordAsync(Entry("a1", action: "DisablePlugin"));
        var ledger = await store.GetAsync();
        Assert.Single(ledger.Entries);
        Assert.Equal("DisablePlugin", ledger.Entries["a1"].Action);
    }

    [Fact]
    public async Task Suppressed_advisory_counts_as_handled()
    {
        // This is the safety valve: suppression must stop the advisory being acted on again.
        // Fix-round note (Finding I3): SuppressAsync no longer fabricates an entry for an id it has
        // never seen (see Suppressing_an_unknown_advisory_id_is_rejected below) - this test now
        // records the advisory first, matching the new "must already exist" contract.
        //
        // Fix-round note (Finding T1): seeded with a NON-terminal status (Unverified) rather than
        // the Entry() helper's default Acted, for consistency with the other corrected fixtures
        // below. This does NOT change what IsHandledAsync discriminates here - it is true for ANY
        // recorded entry regardless of status (see its own doc comment), so that assertion is a
        // sanity check that the entry still exists, not proof SuppressAsync ran, whichever status
        // is seeded. The Entries["a1"].Suppressed assertion is what actually proves the write - it
        // is only ever set by a successful SuppressAsync call, terminal seed status or not.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.Status = LedgerStatus.Unverified;
        await store.RecordAsync(entry);
        Assert.True(await store.SuppressAsync("a1"));
        Assert.True(await store.IsHandledAsync("a1"));
        Assert.True((await store.GetAsync()).Entries["a1"].Suppressed);
    }

    // --- IsActedAsync (Task 11 review, Finding C1): narrower than IsHandledAsync - true only for
    // a TERMINAL status, not for any recorded entry at all. As of Finding R1, the terminal set is
    // Acted, NeedsDecision, NotApplicable, and Suppressed - a status is terminal iff nothing about
    // re-fetching byte-identical content could ever change it; Rejected/Unverified/NeedsAttention
    // are excluded because each is final only because SecSwitch could not evaluate the advisory
    // properly, not because its content settled anything. As of Finding R2, entry.Suppressed is
    // ALSO honoured directly, independent of the Status string - see IsActedAsync's own doc comment.

    [Fact]
    public async Task Empty_ledger_reports_nothing_acted_on()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.IsActedAsync("nope"));
    }

    [Theory]
    [InlineData(LedgerStatus.Acted)]
    [InlineData(LedgerStatus.NeedsDecision)]
    [InlineData(LedgerStatus.NotApplicable)]
    [InlineData(LedgerStatus.Suppressed)]
    public async Task Terminal_status_counts_as_acted_on(string terminalStatus)
    {
        // LedgerEntry is a plain mutable class, not a record - no `with` expression available, so
        // the Status set by the Entry() helper is overwritten by direct assignment instead.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.Status = terminalStatus;
        await store.RecordAsync(entry);
        Assert.True(await store.IsActedAsync("a1"));
    }

    [Theory]
    [InlineData(LedgerStatus.Rejected)]
    [InlineData(LedgerStatus.Unverified)]
    [InlineData(LedgerStatus.NeedsAttention)]
    [InlineData(LedgerStatus.Unsuppressed)]
    [InlineData("")]
    public async Task Non_terminal_status_does_not_count_as_acted_on(string nonTerminalStatus)
    {
        // Finding R1 promotes NotApplicable to terminal - it is intentionally NOT one of the cases
        // here any more (see Terminal_status_counts_as_acted_on above for its inverted, now-correct
        // expectation). Unsuppressed added per Finding T1's symmetry request: all eight
        // LedgerStatus constants are now enumerated across this theory and
        // Terminal_status_counts_as_acted_on (4 terminal + Rejected/Unverified/NeedsAttention/
        // Unsuppressed non-terminal = 8), rather than Unsuppressed being pinned non-terminal only
        // via the standalone Assert.False(IsTerminalStatus(...)) inside
        // Unsuppressing_an_advisory_clears_suppressed_status_and_content_hash below.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.Status = nonTerminalStatus;
        await store.RecordAsync(entry);
        Assert.False(await store.IsActedAsync("a1"));
        // IsHandledAsync still sees it - the two methods answer different questions.
        Assert.True(await store.IsHandledAsync("a1"));
    }

    [Fact]
    public async Task Terminal_status_match_is_case_insensitive()
    {
        // Matches the OrdinalIgnoreCase convention this class already uses for advisory ids
        // (see the class doc comment) - a status string's casing should not be load-bearing either.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.Status = "acted";
        await store.RecordAsync(entry);
        Assert.True(await store.IsActedAsync("a1"));
    }

    [Fact]
    public async Task Suppressed_via_SuppressAsync_counts_as_acted_on()
    {
        // Fix-round note (Finding I3): records the advisory first - see the note on
        // Suppressed_advisory_counts_as_handled above.
        //
        // Fix-round note (Finding T1 - the load-bearing correction, not just style): the
        // Entry() helper defaults to Status = Acted, which is ITSELF terminal - recording it and
        // then asserting IsActedAsync would have passed even if SuppressAsync were gutted to a
        // no-op, because Acted alone already satisfies IsTerminalStatus. Seeded here with the
        // NON-terminal Unverified instead: before SuppressAsync runs, IsActedAsync is provably
        // false (see Non_terminal_status_does_not_count_as_acted_on), so the assertion below only
        // passes if SuppressAsync itself is what flips it - a genuine no-op SuppressAsync would
        // now fail this test.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.Status = LedgerStatus.Unverified;
        await store.RecordAsync(entry);
        Assert.True(await store.SuppressAsync("a1"));
        Assert.True(await store.IsActedAsync("a1"));
    }

    [Fact]
    public async Task Suppressed_flag_without_a_matching_status_string_still_counts_as_acted_on()
    {
        // Task 11 review, Finding R2: IsActedAsync must honour entry.Suppressed directly, not only
        // via the "Suppressed" Status string. Today SuppressAsync is the sole writer and always
        // sets both together, but Suppressed is a public settable property - a future admin-UI
        // suppression path that sets the flag without also setting a matching Status must not
        // silently defeat the escape hatch under this narrower gate. Constructs that exact
        // (currently hypothetical, but structurally possible) mismatch directly.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.Status = LedgerStatus.Unverified; // deliberately NOT a terminal status string
        entry.Suppressed = true;
        await store.RecordAsync(entry);

        Assert.True(await store.IsActedAsync("a1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsActedAsync_with_blank_advisory_id_returns_false(string? advisoryId)
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.IsActedAsync(advisoryId!)); // must not throw
    }

    [Fact]
    public async Task Startup_heartbeat_is_persisted()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        var now = DateTimeOffset.UtcNow;
        await store.RecordStartupAsync(now);
        Assert.Equal(now, (await store.GetAsync()).LastStartedAt);
    }

    // --- Task 13 review, Finding I1 (Important) fix: RecordTrustRootOfferedAsync replaces a single
    // permanent "bootstrap already ran" bool with a per-fingerprint offered set (see
    // SecSwitchLedger.OfferedTrustRootFingerprints's own doc comment for why).

    [Fact]
    public async Task Trust_root_offered_fingerprints_are_persisted()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordTrustRootOfferedAsync(["fp-1", "fp-2"]);

        var offered = (await store.GetAsync()).OfferedTrustRootFingerprints;
        Assert.Contains("fp-1", offered);
        Assert.Contains("fp-2", offered);
    }

    [Fact]
    public async Task Trust_root_offered_fingerprints_accumulate_across_calls_without_duplicating()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordTrustRootOfferedAsync(["fp-1"]);
        await store.RecordTrustRootOfferedAsync(["fp-1", "fp-2"]);

        var offered = (await store.GetAsync()).OfferedTrustRootFingerprints;
        Assert.Equal(2, offered.Count);
        Assert.Contains("fp-1", offered);
        Assert.Contains("fp-2", offered);
    }

    [Fact]
    public async Task Trust_root_offered_fingerprint_match_is_case_insensitive()
    {
        // Matches the OrdinalIgnoreCase convention this plugin uses for every other fingerprint
        // comparison (AdvisoryVerifier, TrustStore, TrustRootBootstrapper).
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordTrustRootOfferedAsync(["ABCDEF"]);
        await store.RecordTrustRootOfferedAsync(["abcdef"]);

        var offered = (await store.GetAsync()).OfferedTrustRootFingerprints;
        Assert.Single(offered);
    }

    [Fact]
    public async Task Trust_root_offered_fingerprints_does_not_disturb_other_ledger_state()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        var now = DateTimeOffset.UtcNow;
        await store.RecordStartupAsync(now);

        await store.RecordTrustRootOfferedAsync(["fp-1"]);

        var ledger = await store.GetAsync();
        Assert.True(ledger.Entries.ContainsKey("a1"));
        Assert.Equal(now, ledger.LastStartedAt);
        Assert.Contains("fp-1", ledger.OfferedTrustRootFingerprints);
    }

    [Fact]
    public async Task Recording_no_new_trust_root_fingerprints_is_a_harmless_no_op()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordTrustRootOfferedAsync([]); // must not throw
        Assert.Empty((await store.GetAsync()).OfferedTrustRootFingerprints);

        await store.RecordTrustRootOfferedAsync(["fp-1"]);
        await store.RecordTrustRootOfferedAsync(["fp-1"]); // already offered - no-op
        Assert.Single((await store.GetAsync()).OfferedTrustRootFingerprints);
    }

    // --- Additional coverage beyond the brief's given tests: aliasing independence and
    // concurrency safety (see task instructions - both flagged as the likely places for a defect).

    [Fact]
    public async Task Recorded_advisory_is_reported_handled_without_relying_on_fake_aliasing()
    {
        var store = new LedgerStore(new JsonRoundTrippingSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        Assert.True(await store.IsHandledAsync("a1"));
        Assert.False(await store.IsHandledAsync("a2"));
    }

    [Fact]
    public async Task Recording_the_same_advisory_twice_does_not_duplicate_without_relying_on_fake_aliasing()
    {
        var store = new LedgerStore(new JsonRoundTrippingSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        await store.RecordAsync(Entry("a1", action: "DisablePlugin"));
        var ledger = await store.GetAsync();
        Assert.Single(ledger.Entries);
        Assert.Equal("DisablePlugin", ledger.Entries["a1"].Action);
    }

    [Fact]
    public async Task Suppressed_advisory_counts_as_handled_without_relying_on_fake_aliasing()
    {
        // Fix-round note (Finding I3): records the advisory first - see the note on
        // Suppressed_advisory_counts_as_handled above.
        // Fix-round note (Finding T1): seeded non-terminal for consistency - see the same note on
        // Suppressed_advisory_counts_as_handled above for why this does (and does not) matter here.
        var store = new LedgerStore(new JsonRoundTrippingSettingsRepository());
        var entry = Entry("a1");
        entry.Status = LedgerStatus.Unverified;
        await store.RecordAsync(entry);
        Assert.True(await store.SuppressAsync("a1"));
        Assert.True(await store.IsHandledAsync("a1"));
        Assert.True((await store.GetAsync()).Entries["a1"].Suppressed);
    }

    [Fact]
    public async Task Startup_heartbeat_is_persisted_without_relying_on_fake_aliasing()
    {
        var store = new LedgerStore(new JsonRoundTrippingSettingsRepository());
        var now = DateTimeOffset.UtcNow;
        await store.RecordStartupAsync(now);
        Assert.Equal(now, (await store.GetAsync()).LastStartedAt);
    }

    [Fact]
    public async Task Concurrent_RecordAsync_calls_for_distinct_advisories_all_survive()
    {
        // Load-bearing concurrency property (see task instructions and LedgerStore's class doc
        // comment): RecordAsync is a read-modify-write and ISettingsRepository itself provides no
        // transaction or lock. Two interleaved read-modify-writes can each read the same "before"
        // ledger, mutate their own in-memory copy, and write back - whichever write lands last
        // wins and silently discards the other's entry. A background poller (later task) and an
        // admin suppressing an advisory from the web UI can call these methods concurrently, so
        // this fires many concurrent RecordAsync calls for DISTINCT advisory ids at ONE shared
        // store and asserts every single one survives. Uses real thread-pool threads (Task.Run),
        // not just concurrent async continuations on one thread, and wraps the repository in
        // SlowSettingsRepository to widen the internal race window - see its doc comment.
        var store = new LedgerStore(new SlowSettingsRepository(new FakeSettingsRepository()));
        const int concurrency = 100;

        var tasks = Enumerable.Range(0, concurrency)
            .Select(i => Task.Run(() => store.RecordAsync(Entry($"a{i}"))));
        await Task.WhenAll(tasks);

        var ledger = await store.GetAsync();
        Assert.Equal(concurrency, ledger.Entries.Count);
        for (var i = 0; i < concurrency; i++)
            Assert.True(ledger.Entries.ContainsKey($"a{i}"), $"advisory a{i} missing - lost update");
    }

    // --- Fix-round additions (post-review) -------------------------------------------------
    //
    // Finding 1 (Important): Entries was keyed case-sensitively, so "ADV-1" and "adv-1" became
    // TWO ledger entries - defeating both of this class's safety properties (an already-acted-on
    // advisory reported under a different casing would look unhandled again; suppressing one
    // casing would not suppress the other). Fixed in LedgerStore.GetAsync by re-establishing an
    // OrdinalIgnoreCase-keyed Entries dictionary on every fetch.

    [Fact]
    public async Task Recorded_advisory_is_reported_handled_regardless_of_casing()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("ADV-1"));
        Assert.True(await store.IsHandledAsync("adv-1"));
        Assert.True(await store.IsHandledAsync("Adv-1"));
    }

    [Fact]
    public async Task Recording_a_different_casing_of_an_existing_advisory_does_not_duplicate()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("ADV-1"));
        await store.RecordAsync(Entry("adv-1", action: "DisablePlugin"));

        var ledger = await store.GetAsync();
        Assert.Single(ledger.Entries);
        Assert.Equal("DisablePlugin", ledger.Entries.Values.Single().Action);
    }

    [Fact]
    public async Task Suppressing_one_casing_suppresses_the_advisory_recorded_under_another_casing()
    {
        // This is the exact scenario Finding 1 called out: an admin suppressing a false positive
        // by whatever casing they see in an alert must suppress the SAME entry the poller
        // recorded, however it was cased.
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("ADV-1"));
        Assert.True(await store.SuppressAsync("adv-1"));

        var ledger = await store.GetAsync();
        Assert.Single(ledger.Entries); // merged into the SAME entry, not a second one
        Assert.True(ledger.Entries.Values.Single().Suppressed);
        Assert.True(await store.IsHandledAsync("ADV-1"));
        Assert.True(await store.IsHandledAsync("adv-1"));
    }

    [Fact]
    public async Task Case_insensitivity_survives_persistence_through_a_json_round_trip()
    {
        // Uses a FRESH LedgerStore instance sharing the same underlying repository, forcing a
        // genuine JSON deserialize on read rather than any in-memory shortcut a single instance
        // might offer - proving normalization happens on READ (inside GetAsync), which is what
        // makes it work regardless of which process or LedgerStore instance reads the ledger back.
        var repo = new JsonRoundTrippingSettingsRepository();
        await new LedgerStore(repo).RecordAsync(Entry("ADV-1"));

        var store = new LedgerStore(repo); // new instance, same underlying persisted JSON
        Assert.True(await store.IsHandledAsync("adv-1"));
    }

    [Fact]
    public async Task Pre_existing_case_variant_duplicate_entries_are_merged_not_thrown_on_read()
    {
        // Self-heal edge case surfaced while implementing Finding 1's fix: a ledger PERSISTED
        // BEFORE this fix shipped could already contain two entries for the same advisory
        // differing only by case (that was exactly the bug). GetAsync must not throw when it
        // re-establishes case-insensitivity over such a ledger - it must merge them instead. (A
        // naive fix using the Dictionary(IDictionary, comparer) copy-constructor would throw
        // ArgumentException on the second colliding key here; see GetAsync's comment.)
        var repo = new FakeSettingsRepository();
        var preExisting = new SecSwitchLedger();
        preExisting.Entries["ADV-1"] = Entry("ADV-1", action: "DisablePlugin");
        preExisting.Entries["adv-1"] = Entry("adv-1", action: "UpdatePlugin");
        await repo.UpdateSetting(preExisting);

        var store = new LedgerStore(repo);
        var ledger = await store.GetAsync(); // must not throw
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public void Newtonsoft_round_trip_does_not_preserve_a_custom_dictionary_comparer()
    {
        // Empirical check requested at review, informing Finding 1's fix: does a custom
        // StringComparer set on SecSwitchLedger.Entries survive a round trip through the SAME
        // Newtonsoft.Json serialization core's real SettingsRepository uses (JsonConvert.
        // SerializeObject / DeserializeObject<T> - see submodules/btcpayserver/BTCPayServer/
        // Services/SettingsRepository.cs)? Observed: it does NOT survive. Newtonsoft has no way
        // to represent a dictionary's comparer in JSON at all (it isn't part of the data), so
        // DeserializeObject reconstructs a plain Dictionary<TKey,TValue> - default (case-
        // sensitive) comparer - and populates it from the parsed pairs, discarding whatever
        // comparer the original instance used. This is exactly why GetAsync re-normalizes Entries
        // on every fetch instead of trusting the model to keep its OrdinalIgnoreCase comparer
        // across a save/load cycle - see task-7-report.md's fix-round section for the command and
        // full output that produced this observation.
        var ledger = new SecSwitchLedger
        {
            Entries = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase)
        };
        ledger.Entries["ADV-1"] = Entry("ADV-1");

        var json = NewtonsoftJsonConvert.SerializeObject(ledger);
        var roundTripped = NewtonsoftJsonConvert.DeserializeObject<SecSwitchLedger>(json)!;

        Assert.True(roundTripped.Entries.ContainsKey("ADV-1"));  // the exact original key survives...
        Assert.False(roundTripped.Entries.ContainsKey("adv-1")); // ...but case-insensitive lookup does not.
    }

    // --- Minor 3: null/blank advisory ids must fail gracefully, not crash.

    [Fact]
    public async Task RecordAsync_with_null_entry_is_a_no_op()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(null!); // must not throw
        Assert.Empty((await store.GetAsync()).Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordAsync_with_blank_advisory_id_is_a_no_op(string? advisoryId)
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry(advisoryId!)); // must not throw
        Assert.Empty((await store.GetAsync()).Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SuppressAsync_with_blank_advisory_id_is_a_no_op(string? advisoryId)
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.SuppressAsync(advisoryId!)); // must not throw
        Assert.Empty((await store.GetAsync()).Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsHandledAsync_with_blank_advisory_id_returns_false(string? advisoryId)
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.IsHandledAsync(advisoryId!)); // must not throw
    }

    // --- Minor 4: coverage gap - RecordStartupAsync must not disturb existing entries.

    [Fact]
    public async Task Startup_heartbeat_does_not_erase_existing_entries()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        var now = DateTimeOffset.UtcNow;
        await store.RecordStartupAsync(now);

        var ledger = await store.GetAsync();
        Assert.Equal(now, ledger.LastStartedAt);
        Assert.True(ledger.Entries.ContainsKey("a1"));
    }

    // --- Task 12 review, Finding I3: Suppress must reject an id with no existing entry (pre-emptive
    // disarm of a future advisory must be impossible), and un-suppress must exist and correctly
    // reset all three fields the suppressed-terminal state touches: Suppressed, Status, ContentHash.

    [Fact]
    public async Task Suppressing_an_unknown_advisory_id_is_rejected()
    {
        var store = new LedgerStore(new FakeSettingsRepository());

        Assert.False(await store.SuppressAsync("never-seen"));

        var ledger = await store.GetAsync();
        Assert.Empty(ledger.Entries); // no entry was fabricated for the unknown id
    }

    [Fact]
    public async Task Suppressing_an_unknown_advisory_id_does_not_disturb_other_entries()
    {
        // A stronger form of the rejection test: proves the reject path really changes nothing at
        // all, not just "no new top-level entry" - an unrelated, already-recorded advisory must be
        // completely unaffected by a rejected suppress call for a different, unknown id.
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1"));

        Assert.False(await store.SuppressAsync("never-seen"));

        var ledger = await store.GetAsync();
        Assert.Single(ledger.Entries);
        Assert.True(ledger.Entries.ContainsKey("a1"));
    }

    [Fact]
    public async Task Unsuppressing_an_advisory_clears_suppressed_status_and_content_hash()
    {
        // The exact three-field contract UnsuppressAsync's own doc comment describes: Suppressed
        // back to false, Status to a NON-terminal value (so IsActedAsync stops latching), and
        // ContentHash cleared (so AdvisoryFetcher's hash-based dedup stops skipping it). Missing any
        // one of the three would make un-suppress a silent no-op for at least one downstream reader.
        var store = new LedgerStore(new FakeSettingsRepository());
        var entry = Entry("a1");
        entry.ContentHash = "hash-a1";
        await store.RecordAsync(entry);
        Assert.True(await store.SuppressAsync("a1"));
        // Sanity check on the fixture itself: suppression really did cache the hash, matching
        // SuppressAsync setting a terminal Status - otherwise this test would not be exercising the
        // scenario UnsuppressAsync's doc comment describes.
        Assert.Equal("hash-a1", (await store.GetAsync()).Entries["a1"].ContentHash);

        Assert.True(await store.UnsuppressAsync("a1"));

        var unsuppressed = (await store.GetAsync()).Entries["a1"];
        Assert.False(unsuppressed.Suppressed);
        Assert.Equal(LedgerStatus.Unsuppressed, unsuppressed.Status);
        Assert.False(LedgerStore.IsTerminalStatus(unsuppressed.Status));
        Assert.Equal("", unsuppressed.ContentHash);
    }

    [Fact]
    public async Task Unsuppressed_advisory_is_no_longer_reported_as_acted_on()
    {
        // Directly exercises the consumer IsActedAsync/IsTerminalStatus exist to gate:
        // SecSwitchMonitor's re-action check. See SecSwitchMonitorTests for the full, end-to-end
        // suppress -> unsuppress -> re-evaluated-by-ProcessAsync scenario.
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1"));
        await store.SuppressAsync("a1");
        Assert.True(await store.IsActedAsync("a1")); // sanity: suppression really did latch

        await store.UnsuppressAsync("a1");

        Assert.False(await store.IsActedAsync("a1"));
        // IsHandledAsync still sees it - an entry still exists, just no longer terminal.
        Assert.True(await store.IsHandledAsync("a1"));
    }

    [Fact]
    public async Task Unsuppressing_an_unknown_advisory_id_is_rejected()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.UnsuppressAsync("never-seen"));
        Assert.Empty((await store.GetAsync()).Entries); // no entry fabricated
    }

    [Fact]
    public async Task Unsuppressing_an_advisory_that_is_not_currently_suppressed_is_rejected()
    {
        // An entry can exist without ever having been suppressed (e.g. Acted from a normal sweep) -
        // there is nothing to reverse, and this must not be treated as a successful no-op.
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.RecordAsync(Entry("a1")); // Status = Acted, Suppressed = false (see Entry() helper)

        Assert.False(await store.UnsuppressAsync("a1"));

        var entry = (await store.GetAsync()).Entries["a1"];
        Assert.Equal(LedgerStatus.Acted, entry.Status); // untouched
        Assert.False(entry.Suppressed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnsuppressAsync_with_blank_advisory_id_is_a_no_op(string? advisoryId)
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.UnsuppressAsync(advisoryId!)); // must not throw
        Assert.Empty((await store.GetAsync()).Entries);
    }

    [Fact]
    public async Task Suppress_then_unsuppress_survives_a_json_round_trip()
    {
        // Uses a FRESH LedgerStore instance sharing the same underlying repository each time,
        // forcing a genuine JSON deserialize/serialize on every call rather than any in-memory
        // aliasing shortcut - see JsonRoundTrippingSettingsRepository's own doc comment for why this
        // matters (the same reasoning Finding 1's case-insensitivity tests rely on).
        var repo = new JsonRoundTrippingSettingsRepository();
        var entry = Entry("a1");
        entry.ContentHash = "hash-a1";
        await new LedgerStore(repo).RecordAsync(entry);
        Assert.True(await new LedgerStore(repo).SuppressAsync("a1"));

        Assert.True(await new LedgerStore(repo).UnsuppressAsync("a1"));

        var final = (await new LedgerStore(repo).GetAsync()).Entries["a1"];
        Assert.False(final.Suppressed);
        Assert.Equal(LedgerStatus.Unsuppressed, final.Status);
        Assert.Equal("", final.ContentHash);
    }

    // --- Final whole-branch review, Finding C3 (Critical): the feed-index cursor is persisted, so a
    // process restart cannot reset the scan back onto a stuck prefix. ---

    [Fact]
    public async Task Feed_index_cursor_defaults_to_zero_for_a_ledger_that_predates_it()
    {
        // An existing ledger row deserialised without this field must start at the top of the index -
        // both a valid cursor and exactly the pre-fix behaviour.
        Assert.Equal(0, (await new LedgerStore(new FakeSettingsRepository()).GetAsync()).FeedIndexCursor);
    }

    [Fact]
    public async Task Feed_index_cursor_survives_a_json_round_trip()
    {
        // A cursor that does not actually persist is the same permanent starvation with extra steps -
        // every process start would resume from the stuck prefix.
        var repo = new JsonRoundTrippingSettingsRepository();
        await new LedgerStore(repo).RecordFeedIndexCursorAsync(50);

        Assert.Equal(50, (await new LedgerStore(repo).GetAsync()).FeedIndexCursor);
    }

    [Fact]
    public async Task Recording_an_unchanged_feed_index_cursor_writes_nothing()
    {
        // A healthy feed whose whole index fits in one poll returns the same cursor every time;
        // without this check that would be a settings write every hour, for no change.
        var repo = new CountingSettingsRepository();
        var store = new LedgerStore(repo);
        await store.RecordFeedIndexCursorAsync(7);
        var afterFirst = repo.Writes;

        await store.RecordFeedIndexCursorAsync(7);

        Assert.Equal(afterFirst, repo.Writes);
        Assert.Equal(7, (await store.GetAsync()).FeedIndexCursor);
    }

    [Fact]
    public async Task Recording_the_feed_index_cursor_does_not_disturb_the_entries()
    {
        var repo = new FakeSettingsRepository();
        var store = new LedgerStore(repo);
        await store.RecordAsync(Entry("a1"));

        await store.RecordFeedIndexCursorAsync(3);

        var ledger = await store.GetAsync();
        Assert.Equal(3, ledger.FeedIndexCursor);
        Assert.True(ledger.Entries.ContainsKey("a1"));
    }

    /// Counts UpdateSetting calls so a "skips the write when nothing changed" claim can be proven
    /// rather than assumed. Otherwise identical to FakeSettingsRepository.
    sealed class CountingSettingsRepository : ISettingsRepository
    {
        readonly Dictionary<string, object> _store = new();
        public int Writes { get; private set; }

        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
            => Task.FromResult(_store.TryGetValue(name ?? typeof(T).FullName!, out var v) ? (T?)v : null);

        public Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            Writes++;
            _store[name ?? typeof(T).FullName!] = obj;
            return Task.CompletedTask;
        }

        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
            => throw new NotSupportedException();
    }
}
