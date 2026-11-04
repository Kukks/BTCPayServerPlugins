using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public sealed class LedgerStore(ISettingsRepository settingsRepository)
{
    // Static: LedgerStore instances share one persisted ledger, so they must share one gate.
    static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<SecSwitchLedger> GetAsync()
    {
        var ledger = await settingsRepository.GetSettingAsync<SecSwitchLedger>() ?? new SecSwitchLedger();
        var entries = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in ledger.Entries ?? [])
        {
            if (entry is not null)
                entries[id] = entry;
        }
        ledger.Entries = entries;
        return ledger;
    }

    // A suppression an admin made while a poll was in flight must survive the poll's own write.
    public Task RecordAsync(LedgerEntry entry) => MutateAsync(ledger =>
    {
        entry.Suppressed |= ledger.Entries.TryGetValue(entry.AdvisoryId, out var current) && current.Suppressed;
        ledger.Entries[entry.AdvisoryId] = entry;
        return true;
    });

    /// <summary>
    /// Lock flags only mean something while SecSwitch is enabled and automatic; dropping them otherwise keeps
    /// stale ones from resurfacing (and from lying in the banner) when the settings change back.
    /// </summary>
    public Task<bool> ClearLocksAsync(string reason) => MutateAsync(ledger =>
    {
        var locked = ledger.Entries.Values.Where(e => e.LockedDown).ToList();
        foreach (var entry in locked)
        {
            entry.LockedDown = false;
            entry.Status = LedgerStatus.Resolved;
            entry.Reason = reason;
            entry.RecordedAt = DateTimeOffset.UtcNow;
        }
        return locked.Count > 0;
    });

    public Task RecordPollAsync(DateTimeOffset at, string? error) => MutateAsync(ledger =>
    {
        ledger.LastPollAt = at;
        ledger.LastPollError = error;
        return true;
    });

    public Task<bool> SuppressAsync(string? advisoryId) => MutateAsync(ledger =>
    {
        if (!ledger.Entries.TryGetValue(advisoryId ?? "", out var entry))
            return false;
        entry.Suppressed = true;
        return true;
    });

    /// <summary>Forgets a suppressed entry, so the advisory is evaluated from scratch on the next poll.</summary>
    public Task<bool> UnsuppressAsync(string? advisoryId) => MutateAsync(ledger =>
        ledger.Entries.TryGetValue(advisoryId ?? "", out var entry) && entry.Suppressed && ledger.Entries.Remove(advisoryId!));

    async Task<bool> MutateAsync(Func<SecSwitchLedger, bool> mutation)
    {
        await Gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            if (!mutation(ledger))
                return false;
            await settingsRepository.UpdateSetting(ledger);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
