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
    static readonly SemaphoreSlim PolicyGate = new(1, 1);
    // BTCPay's repository can fill its cache after a concurrent write invalidates it.
    static readonly SemaphoreSlim PolicyCacheGate = new(1, 1);

    public async Task<SecSwitchSettings> GetPolicyAsync()
    {
        await PolicyCacheGate.WaitAsync();
        try
        {
            return await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        }
        finally
        {
            PolicyCacheGate.Release();
        }
    }

    internal async Task SavePolicyAsync(SecSwitchSettings settings)
    {
        await PolicyCacheGate.WaitAsync();
        try
        {
            await settingsRepository.UpdateSetting(settings);
        }
        finally
        {
            PolicyCacheGate.Release();
        }
    }

    public async Task<T> WithPolicyAsync<T>(Func<SecSwitchSettings, Task<T>> action)
    {
        await PolicyGate.WaitAsync();
        try
        {
            return await action(await GetPolicyAsync());
        }
        finally
        {
            PolicyGate.Release();
        }
    }

    public async Task<SecSwitchLedger> GetAsync()
    {
        await Gate.WaitAsync();
        try
        {
            return await ReadAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    async Task<SecSwitchLedger> ReadAsync()
    {
        var ledger = await settingsRepository.GetSettingAsync<SecSwitchLedger>() ?? new SecSwitchLedger();
        var entries = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in ledger.Entries ?? [])
        {
            if (entry is not null)
                entries[id] = entry;
        }
        ledger.Entries = entries;
        ledger.LastCoreUpdateAt ??= entries.Values
            .Where(e => e.Action == nameof(SecSwitchAction.UpdateCore) && e.Status == LedgerStatus.Acted)
            .Max(e => (DateTimeOffset?)e.RecordedAt);
        return ledger;
    }

    // A suppression an admin made while a poll was in flight must survive the poll's own write.
    public Task RecordAsync(LedgerEntry entry) => MutateAsync(ledger => RecordEntry(ledger, entry));

    public Task<bool> RecordQueuedOutcomeAsync(LedgerEntry entry) => MutateAsync(ledger =>
        (!ledger.Entries.TryGetValue(entry.AdvisoryId, out var current) || current.RecordedAt < entry.RecordedAt) &&
        RecordEntry(ledger, entry));

    static bool RecordEntry(SecSwitchLedger ledger, LedgerEntry entry)
    {
        entry.Suppressed |= ledger.Entries.TryGetValue(entry.AdvisoryId, out var current) && current.Suppressed;
        entry.NotificationPending = !entry.Suppressed &&
                                    entry.Status is LedgerStatus.Acted or LedgerStatus.NeedsDecision or LedgerStatus.NeedsAttention;
        ledger.Entries[entry.AdvisoryId] = entry;
        return true;
    }

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
            entry.NotificationPending = false;
            entry.Status = LedgerStatus.Resolved;
            entry.Reason = reason;
            entry.RecordedAt = DateTimeOffset.UtcNow;
        }
        return locked.Count > 0;
    });

    public Task RecordPollAsync(DateTimeOffset at, string? error, string? feedUrl = null, Guid? policyRevision = null,
        int skippedEntries = 0) => MutateAsync(ledger =>
    {
        ledger.LastPollAt = at;
        ledger.LastPollError = error;
        ledger.LastPollFeedUrl = feedUrl;
        ledger.LastPollPolicyRevision = policyRevision;
        ledger.LastPollSkippedEntries = skippedEntries;
        return true;
    });

    public Task RecordCoreUpdateAsync(DateTimeOffset at) => MutateAsync(ledger =>
    {
        if (ledger.LastCoreUpdateAt is { } last && last >= at)
            return false;
        ledger.LastCoreUpdateAt = at;
        return true;
    });

    public Task ReleaseCoreUpdateAsync(DateTimeOffset reservation, DateTimeOffset? previous) => MutateAsync(ledger =>
    {
        if (ledger.LastCoreUpdateAt != reservation)
            return false;
        ledger.LastCoreUpdateAt = previous;
        return true;
    });

    public Task<bool> MarkNotifiedAsync(string advisoryId, DateTimeOffset recordedAt) => MutateAsync(ledger =>
    {
        if (!ledger.Entries.TryGetValue(advisoryId, out var entry) ||
            !entry.NotificationPending || entry.RecordedAt != recordedAt)
            return false;
        entry.NotificationPending = false;
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
            var ledger = await ReadAsync();
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
