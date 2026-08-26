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
        Status = "Acted", Action = action, Reason = "r", RecordedAt = DateTimeOffset.UtcNow
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
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.SuppressAsync("a1");
        Assert.True(await store.IsHandledAsync("a1"));
        Assert.True((await store.GetAsync()).Entries["a1"].Suppressed);
    }

    // --- IsActedAsync (Task 11 review, Finding C1): narrower than IsHandledAsync - true only for
    // a TERMINAL status (Acted, NeedsDecision, Suppressed), not for any recorded entry at all.

    [Fact]
    public async Task Empty_ledger_reports_nothing_acted_on()
    {
        var store = new LedgerStore(new FakeSettingsRepository());
        Assert.False(await store.IsActedAsync("nope"));
    }

    [Theory]
    [InlineData("Acted")]
    [InlineData("NeedsDecision")]
    [InlineData("Suppressed")]
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
    [InlineData("Rejected")]
    [InlineData("Unverified")]
    [InlineData("NotApplicable")]
    [InlineData("NeedsAttention")]
    [InlineData("")]
    public async Task Non_terminal_status_does_not_count_as_acted_on(string nonTerminalStatus)
    {
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
        var store = new LedgerStore(new FakeSettingsRepository());
        await store.SuppressAsync("a1");
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
        var store = new LedgerStore(new JsonRoundTrippingSettingsRepository());
        await store.SuppressAsync("a1");
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
        await store.SuppressAsync("adv-1");

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
        await store.SuppressAsync(advisoryId!); // must not throw
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
}
