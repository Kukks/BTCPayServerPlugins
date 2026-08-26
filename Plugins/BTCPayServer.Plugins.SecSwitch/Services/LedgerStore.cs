using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// Persists which advisories SecSwitch has already seen and what was done about them, via
/// BTCPayServer's <see cref="ISettingsRepository"/>. This is a safety mechanism, not bookkeeping:
///
/// 1. It prevents an action loop. If SecSwitch shuts the server down over an unfixable core
///    advisory and the deployment's restart policy brings it back up, the ledger is the only
///    thing stopping it re-acting on the same advisory forever - a crash loop rather than a kill
///    switch.
/// 2. <see cref="SuppressAsync"/> is the admin's escape hatch: a local-only override so a false
///    positive can never permanently lock an operator out of running their own server.
///    Suppression makes <see cref="IsHandledAsync"/> return true, so a suppressed advisory is
///    never acted on again.
///
/// Concurrency: <see cref="RecordAsync"/>, <see cref="SuppressAsync"/> and
/// <see cref="RecordStartupAsync"/> are all read-modify-write against <see cref="ISettingsRepository"/>
/// (fetch the whole ledger, mutate a copy, write the whole ledger back) and ISettingsRepository
/// itself provides no transaction or optimistic-concurrency check - its UpdateSetting is an
/// unconditional last-writer-wins upsert (see core's Services/SettingsRepository.cs). A later
/// task calls these methods from a background poller while an admin may, at the same time, call
/// SuppressAsync from the web UI. Two interleaved read-modify-writes can each fetch the same
/// "before" ledger, mutate their own separate in-memory copy, and write back - whichever write
/// lands last overwrites the other and silently discards its entry. For this ledger that means an
/// advisory being silently re-acted on, which is exactly the failure mode this class exists to
/// prevent. <see cref="_gate"/> serializes every read-modify-write below so that cannot happen:
/// each of the three methods holds it for the full fetch-mutate-save sequence, so a second caller
/// always starts its own read-modify-write from a ledger that already includes every write that
/// finished before it, rather than from a stale snapshot.
///
/// Limits of that gate, stated explicitly: it is an in-process <see cref="SemaphoreSlim"/>, so it
/// only serializes callers that share this one LedgerStore instance. ISettingsRepository is
/// registered as a singleton in core, so within a single BTCPayServer process there is exactly
/// one LedgerStore and therefore exactly one gate guarding every caller in that process - the
/// poller and the web UI above are safe. It does NOT serialize across multiple OS processes (e.g.
/// a hypothetical multi-instance/load-balanced deployment sharing one database): two processes
/// each have their own independent gate and could still race each other's UpdateSetting calls.
/// SecSwitch's threat model is a single self-hosted instance, which makes that an accepted gap
/// rather than a defect here, but it is a real limit of this mechanism worth being explicit about
/// rather than silently assuming the gate covers.
/// </summary>
public sealed class LedgerStore(ISettingsRepository settingsRepository)
{
    // Guards every read-modify-write sequence below (GetAsync -> mutate -> UpdateSetting) so that
    // two concurrent writers cannot lose an entry - see the class doc comment for why this
    // matters and what it does and does not protect against. A plain read (GetAsync /
    // IsHandledAsync) deliberately does NOT take this gate: it never writes anything back, so it
    // cannot itself cause a lost update, and ISettingsRepository.UpdateSetting's single-row write
    // is what determines whether such a read observes the old or the new ledger - never a value
    // torn between the two.
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SecSwitchLedger> GetAsync()
        => await settingsRepository.GetSettingAsync<SecSwitchLedger>() ?? new SecSwitchLedger();

    public async Task RecordAsync(LedgerEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            ledger.Entries[entry.AdvisoryId] = entry;
            // Always saved back explicitly - this must never depend on the mutation above alone
            // being "enough". GetAsync's returned object is not guaranteed to be the same
            // instance the repository holds internally: the real SettingsRepository deserializes
            // a fresh object graph from JSON on every read, so mutating it does nothing to what
            // is actually persisted until UpdateSetting is called (only a naive in-memory test
            // fake that hands back the same reference every time could make that mistake look
            // like it works). See LedgerStoreTests' JsonRoundTrippingSettingsRepository.
            await settingsRepository.UpdateSetting(ledger);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsHandledAsync(string advisoryId)
        => (await GetAsync()).Entries.ContainsKey(advisoryId);

    public async Task SuppressAsync(string advisoryId)
    {
        await _gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            if (!ledger.Entries.TryGetValue(advisoryId, out var entry))
            {
                entry = new LedgerEntry { AdvisoryId = advisoryId, RecordedAt = DateTimeOffset.UtcNow };
                ledger.Entries[advisoryId] = entry;
            }
            entry.Suppressed = true;
            entry.Status = "Suppressed";
            await settingsRepository.UpdateSetting(ledger);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordStartupAsync(DateTimeOffset now)
    {
        await _gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            ledger.LastStartedAt = now;
            await settingsRepository.UpdateSetting(ledger);
        }
        finally
        {
            _gate.Release();
        }
    }
}
