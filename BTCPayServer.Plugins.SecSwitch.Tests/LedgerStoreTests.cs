using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class LedgerStoreTests
{
    readonly FakeSettingsRepository _repository = new();

    static LedgerEntry Entry(string id = "a1", string status = LedgerStatus.Acted) =>
        new() { AdvisoryId = id, Status = status, Title = "t" };

    [Fact]
    public async Task A_fresh_ledger_is_empty()
    {
        Assert.Empty((await new LedgerStore(_repository).GetAsync()).Entries);
    }

    [Fact]
    public async Task Entries_are_persisted_and_replaced_by_id_ignoring_case()
    {
        var store = new LedgerStore(_repository);

        await store.RecordAsync(Entry("ABC", LedgerStatus.NeedsAttention));
        await store.RecordAsync(Entry("abc", LedgerStatus.Acted));

        var entries = (await store.GetAsync()).Entries;
        Assert.Equal(LedgerStatus.Acted, Assert.Single(entries).Value.Status);
        Assert.Equal(LedgerStatus.Acted, entries["Abc"].Status);
    }

    [Fact]
    public async Task Suppress_marks_a_known_entry_and_refuses_unknown_ids()
    {
        var store = new LedgerStore(_repository);
        await store.RecordAsync(Entry("a1"));

        Assert.True(await store.SuppressAsync("a1"));
        Assert.False(await store.SuppressAsync("unknown"));
        Assert.False(await store.SuppressAsync(null));
        Assert.True((await store.GetAsync()).Entries["a1"].Suppressed);
        Assert.Single((await store.GetAsync()).Entries);
    }

    [Fact]
    public async Task Unsuppress_forgets_a_suppressed_entry_so_it_is_evaluated_afresh()
    {
        var store = new LedgerStore(_repository);
        await store.RecordAsync(Entry("a1"));
        await store.RecordAsync(Entry("a2"));
        await store.SuppressAsync("a1");

        Assert.True(await store.UnsuppressAsync("a1"));
        Assert.False(await store.UnsuppressAsync("a2"));
        Assert.False(await store.UnsuppressAsync("unknown"));
        Assert.False(await store.UnsuppressAsync(null));
        Assert.Equal("a2", Assert.Single((await store.GetAsync()).Entries).Key);
    }

    [Fact]
    public async Task Clearing_locks_resolves_every_locking_entry_and_leaves_the_rest_alone()
    {
        var store = new LedgerStore(_repository);
        await store.RecordAsync(new LedgerEntry { AdvisoryId = "core-1", Status = LedgerStatus.Acted, LockedDown = true });
        await store.RecordAsync(Entry("plug-1"));

        Assert.True(await store.ClearLocksAsync("lifted"));
        Assert.False(await store.ClearLocksAsync("lifted"));

        var entries = (await store.GetAsync()).Entries;
        Assert.False(entries["core-1"].LockedDown);
        Assert.Equal(LedgerStatus.Resolved, entries["core-1"].Status);
        Assert.Equal("lifted", entries["core-1"].Reason);
        Assert.Equal(LedgerStatus.Acted, entries["plug-1"].Status);
    }

    [Fact]
    public async Task Re_recording_an_entry_keeps_a_suppression_made_in_the_meantime()
    {
        var store = new LedgerStore(_repository);
        await store.RecordAsync(Entry("a1", LedgerStatus.NeedsAttention));
        await store.SuppressAsync("a1");

        await store.RecordAsync(Entry("a1", LedgerStatus.Acted));

        var entry = (await store.GetAsync()).Entries["a1"];
        Assert.True(entry.Suppressed);
        Assert.Equal(LedgerStatus.Acted, entry.Status);
    }

    [Fact]
    public async Task The_outcome_of_the_last_poll_is_persisted_and_a_success_clears_the_error()
    {
        var store = new LedgerStore(_repository);
        var at = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

        await store.RecordPollAsync(at, "404 Not Found");
        var failed = await store.GetAsync();
        await store.RecordPollAsync(at.AddHours(1), null);
        var succeeded = await store.GetAsync();

        Assert.Equal(at, failed.LastPollAt);
        Assert.Equal("404 Not Found", failed.LastPollError);
        Assert.Equal(at.AddHours(1), succeeded.LastPollAt);
        Assert.Null(succeeded.LastPollError);
    }

    [Fact]
    public async Task Null_entries_and_a_null_map_in_the_persisted_ledger_are_tolerated()
    {
        await _repository.UpdateSetting(new SecSwitchLedger
        {
            Entries = new Dictionary<string, LedgerEntry> { ["good"] = Entry("good"), ["bad"] = null! }
        });
        var store = new LedgerStore(_repository);
        Assert.Equal("good", Assert.Single((await store.GetAsync()).Entries).Key);

        await _repository.UpdateSetting(new SecSwitchLedger { Entries = null! });
        Assert.Empty((await store.GetAsync()).Entries);
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_updates()
    {
        var store = new LedgerStore(new YieldingRepository(_repository));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.RecordAsync(Entry($"a{i}"))));

        Assert.Equal(20, (await store.GetAsync()).Entries.Count);
    }

    sealed class YieldingRepository(ISettingsRepository inner) : ISettingsRepository
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
            => throw new NotSupportedException();
    }
}
