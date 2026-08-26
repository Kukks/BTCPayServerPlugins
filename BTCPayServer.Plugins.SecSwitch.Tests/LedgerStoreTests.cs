using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

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
}
