using System;
using System.Collections.Generic;
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
///    Suppression makes both <see cref="IsHandledAsync"/> and <see cref="IsActedAsync"/> return
///    true, so a suppressed advisory is never acted on again (see <see cref="IsActedAsync"/>'s own
///    doc comment - it, not <see cref="IsHandledAsync"/>, is what a re-action check should use).
///
/// Both properties depend on advisory ids being compared case-INsensitively, matching the
/// OrdinalIgnoreCase convention already used for every other identifier in this plugin
/// (TrustStore, PolicyResolver, AdvisoryVerifier, AdvisoryApplicability). "ADV-1" and "adv-1" must
/// resolve to the SAME ledger entry, or a differently-cased resubmission could bypass both the
/// action-loop guard and an admin's suppression. <see cref="GetAsync"/> re-establishes an
/// OrdinalIgnoreCase-keyed `Entries` dictionary on every fetch rather than trusting
/// SecSwitchLedger.Entries's own comparer, because that comparer does not reliably survive a real
/// JSON round trip - verified empirically against the same Newtonsoft.Json serialization core's
/// SettingsRepository actually uses (see LedgerStoreTests.Newtonsoft_round_trip_does_not_preserve_a_custom_dictionary_comparer
/// and task-7-report.md's fix-round section): a fresh SecSwitchLedger coming back from
/// GetSettingAsync&lt;T&gt; can arrive with a case-SENSITIVE Entries dictionary regardless of how
/// Entries was constructed before it was persisted, so LedgerStore re-establishes
/// case-insensitivity itself on every read instead of relying on the model.
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
/// <see cref="_gate"/> is `static`, deliberately, not an instance field: there is exactly one
/// logical ledger (a single settings row keyed by type name) no matter how many LedgerStore
/// OBJECTS happen to exist, so one process-wide gate is not just safe but semantically correct -
/// and, unlike an instance field, its correctness does not depend on LedgerStore itself being
/// registered in DI as a singleton. Nothing registers LedgerStore in DI yet. If a later task
/// registers it as Scoped or Transient - an easy default that ASP.NET Core's DI validation does
/// NOT flag, since injecting a singleton ISettingsRepository into a scoped/transient service is
/// perfectly legal - an instance-field gate would silently hand every caller its own private gate
/// and none of the protection described above would actually happen, with no compiler error and
/// nothing in this test suite able to catch it (every test constructs LedgerStore directly rather
/// than through DI). A static field is immune to that by construction: it is shared by every
/// LedgerStore instance in the process regardless of how any of them were constructed.
///
/// Limits of that gate, stated explicitly: it is a single in-process <see cref="SemaphoreSlim"/>,
/// so it only serializes callers within ONE BTCPayServer process. It does NOT serialize across
/// multiple OS processes (e.g. a hypothetical multi-instance/load-balanced deployment sharing one
/// database): two processes each have their own independent gate and could still race each
/// other's UpdateSetting calls. SecSwitch's threat model is a single self-hosted instance, which
/// makes that an accepted gap rather than a defect here, but it is a real limit of this mechanism
/// worth being explicit about rather than silently assuming the gate covers.
/// </summary>
public sealed class LedgerStore(ISettingsRepository settingsRepository)
{
    // Guards every read-modify-write sequence below (GetAsync -> mutate -> UpdateSetting) so that
    // two concurrent writers cannot lose an entry - see the class doc comment for why this is
    // `static` (independent of LedgerStore's own DI lifetime) and what it does and does not
    // protect against. A plain read (GetAsync / IsHandledAsync) deliberately does NOT take this
    // gate: it never writes anything back, so it cannot itself cause a lost update, and
    // ISettingsRepository.UpdateSetting's single-row write is what determines whether such a read
    // observes the old or the new ledger - never a value torn between the two.
    static readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SecSwitchLedger> GetAsync()
    {
        var ledger = await settingsRepository.GetSettingAsync<SecSwitchLedger>() ?? new SecSwitchLedger();

        // Re-establish case-insensitive keys on every fetch - see the class doc comment for why
        // this cannot be delegated to SecSwitchLedger.Entries's own comparer. Rebuilt key-by-key
        // via indexer assignment rather than the Dictionary(IDictionary, comparer) copy-constructor
        // overload: a ledger persisted BEFORE this fix could already contain two entries for the
        // same advisory differing only by case (that was exactly the bug), and the copy-constructor
        // overload throws ArgumentException the moment it meets such a colliding key. Indexer
        // assignment instead merges them (last one wins), self-healing a pre-existing corrupt
        // ledger on read instead of making every future GetAsync call throw.
        if (!ReferenceEquals(ledger.Entries.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ledger.Entries)
                normalized[pair.Key] = pair.Value;
            ledger.Entries = normalized;
        }

        return ledger;
    }

    public async Task RecordAsync(LedgerEntry entry)
    {
        // Fail gracefully, not crash: a null entry or a null/blank AdvisoryId can never be looked
        // back up by IsHandledAsync/SuppressAsync (both also reject null/blank - see below), so
        // recording one would be a dead, unreachable ledger row at best and a NullReferenceException
        // at worst. Treated as a no-op rather than thrown, matching the fail-closed-without-crashing
        // posture the rest of this plugin uses (TrustStore, PolicyResolver, AdvisoryApplicability).
        if (entry is null || string.IsNullOrWhiteSpace(entry.AdvisoryId))
            return;

        await _gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            ledger.Entries[entry.AdvisoryId] = entry;
            // Always saved back explicitly - see class doc comment: GetAsync's returned object is
            // not guaranteed to be the same instance the repository holds internally, so mutating
            // it does nothing to what is actually persisted until UpdateSetting is called.
            await settingsRepository.UpdateSetting(ledger);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsHandledAsync(string advisoryId)
    {
        if (string.IsNullOrWhiteSpace(advisoryId))
            return false;
        return (await GetAsync()).Entries.ContainsKey(advisoryId);
    }

    /// <summary>
    /// True only if <paramref name="advisoryId"/> has a ledger entry recorded under a TERMINAL
    /// status - <c>Acted</c>, <c>NeedsDecision</c>, <c>NotApplicable</c>, or <c>Suppressed</c> (see
    /// <see cref="IsTerminalStatus"/>) - OR carries <see cref="LedgerEntry.Suppressed"/> directly
    /// (Task 11 review, Finding R2: checked independently of the Status string, because
    /// <see cref="LedgerEntry.Suppressed"/> is a public settable property and today's ONLY writer,
    /// <see cref="SuppressAsync"/>, happens to set both together - a future admin-UI suppression
    /// path that sets the flag without also setting the matching Status string must not silently
    /// defeat the escape hatch under this narrower gate). Deliberately narrower than
    /// <see cref="IsHandledAsync"/>, which returns true for ANY recorded entry regardless of status:
    /// a non-terminal entry (Rejected, Unverified, or NeedsAttention) means the advisory was looked
    /// at but we could not evaluate it properly, so it must remain eligible for re-evaluation on a
    /// later poll. Gating a re-action check on <see cref="IsHandledAsync"/> instead would let a
    /// single transient failure - a hostile mirror serving malformed bytes for one poll, or a
    /// signature fetch truncated by the fetcher's own request budget - permanently and silently
    /// disarm SecSwitch for that advisory id: the very re-fetch a withheld ContentHash exists to buy
    /// would arrive at a poller that now refuses to even look at it again, because SOME entry
    /// already exists under that id.
    ///
    /// A caller that wants "has ANY entry ever been recorded, no matter the status" should keep
    /// using <see cref="IsHandledAsync"/>; the two methods answer different questions and are not
    /// interchangeable.
    /// </summary>
    public async Task<bool> IsActedAsync(string advisoryId)
    {
        if (string.IsNullOrWhiteSpace(advisoryId))
            return false;
        var ledger = await GetAsync();
        return ledger.Entries.TryGetValue(advisoryId, out var entry) &&
            (entry.Suppressed || IsTerminalStatus(entry.Status));
    }

    /// <summary>
    /// True for <see cref="LedgerStatus.Acted"/>, <see cref="LedgerStatus.NeedsDecision"/>,
    /// <see cref="LedgerStatus.Suppressed"/>, and - as of Task 11 review Finding R1 -
    /// <see cref="LedgerStatus.NotApplicable"/>. Shared by <see cref="IsActedAsync"/> (gates
    /// re-ACTING: should <c>SecSwitchMonitor</c> even look at this advisory again) and, via this
    /// same predicate, by <c>SecSwitchMonitor.RecordAsync</c> (gates hash-CACHING: may its
    /// ContentHash be persisted, letting <c>AdvisoryFetcher</c> skip re-downloading it). The two
    /// concerns share ONE predicate deliberately, promoted `internal` for exactly that reuse - see
    /// <c>SecSwitchMonitor.RecordAsync</c>'s own doc comment for the full reasoning behind which
    /// statuses belong here and why <c>NotApplicable</c> was added: in short, a status is terminal
    /// here iff nothing about re-fetching BYTE-IDENTICAL content could ever change it. Deliberately
    /// excludes <see cref="LedgerStatus.Rejected"/>, <see cref="LedgerStatus.Unverified"/>, and
    /// <see cref="LedgerStatus.NeedsAttention"/> - each of those is final only because SecSwitch
    /// could not look properly (a parse failure, a quorum failure, or an indeterminate installed
    /// version/instance state), not because the content itself settles anything, so each must
    /// remain eligible for both re-action and re-fetch.
    /// </summary>
    internal static bool IsTerminalStatus(string? status) =>
        string.Equals(status, LedgerStatus.Acted, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, LedgerStatus.NeedsDecision, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, LedgerStatus.NotApplicable, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, LedgerStatus.Suppressed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Suppresses an advisory that SecSwitch has already recorded - the admin's escape hatch (see
    /// the class doc comment). Returns <c>false</c>, and changes nothing, if
    /// <paramref name="advisoryId"/> is blank OR has no existing ledger entry.
    ///
    /// The "must already exist" half is deliberate (Task 12 review, Finding I3): the prior version
    /// of this method created a new entry for an unknown id, which meant the id space accepted here
    /// was unbounded - anything an admin (or a CSRF'd/misclicked request) typed. Advisory ids
    /// ultimately come from the feed (see AdvisoryFetcher), a namespace SecSwitch does not control,
    /// so accepting an arbitrary never-seen id would let a typo'd or deliberately-chosen id
    /// pre-emptively and permanently disarm an advisory that has not even been published yet - the
    /// exact opposite of what an escape hatch is for. Requiring a pre-existing entry closes that:
    /// an id can only be suppressed after SecSwitch itself has actually recorded seeing it.
    /// </summary>
    public async Task<bool> SuppressAsync(string advisoryId)
    {
        if (string.IsNullOrWhiteSpace(advisoryId))
            return false;

        await _gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            if (!ledger.Entries.TryGetValue(advisoryId, out var entry))
                return false; // Finding I3: never fabricate an entry for an id we have not seen.

            entry.Suppressed = true;
            entry.Status = LedgerStatus.Suppressed;
            await settingsRepository.UpdateSetting(ledger);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The inverse of <see cref="SuppressAsync"/> (Task 12 review, Finding I3): reverses a mistaken
    /// or no-longer-wanted suppression so the advisory becomes eligible for automatic
    /// re-evaluation again. Returns <c>false</c>, and changes nothing, if
    /// <paramref name="advisoryId"/> is blank, has no existing entry, or is not currently
    /// suppressed - there is nothing to reverse in any of those cases.
    ///
    /// Un-suppressing is NOT simply "set Suppressed back to false". <see cref="SuppressAsync"/> also
    /// sets <see cref="LedgerEntry.Status"/> to <see cref="LedgerStatus.Suppressed"/>, which is a
    /// TERMINAL status (<see cref="IsTerminalStatus"/>) - and a terminal, signatures-complete status
    /// is exactly the condition under which <c>SecSwitchMonitor.RecordAsync</c> persists
    /// <see cref="LedgerEntry.ContentHash"/> (see its own doc comment). So a suppressed entry's
    /// ContentHash was cached the moment it was suppressed, which means <c>AdvisoryFetcher</c>'s own
    /// hash-based dedup will keep silently skipping this advisory's directory on every future poll -
    /// the advisory would never even be re-DOWNLOADED, regardless of what this method does to
    /// <see cref="LedgerEntry.Status"/>. Un-suppressing must therefore clear all THREE of:
    /// <see cref="LedgerEntry.Suppressed"/> (back to <c>false</c>), <see cref="LedgerEntry.Status"/>
    /// (to the non-terminal <see cref="LedgerStatus.Unsuppressed"/>, so <see cref="IsActedAsync"/>
    /// stops latching on it), AND <see cref="LedgerEntry.ContentHash"/> (back to empty, so the next
    /// poll's fetch is not skipped). Missing the third makes un-suppress a silent no-op:
    /// <see cref="IsActedAsync"/> would correctly start reporting <c>false</c>, but the advisory
    /// would never actually be re-fetched for <c>SecSwitchMonitor</c> to re-evaluate in the first
    /// place.
    /// </summary>
    public async Task<bool> UnsuppressAsync(string advisoryId)
    {
        if (string.IsNullOrWhiteSpace(advisoryId))
            return false;

        await _gate.WaitAsync();
        try
        {
            var ledger = await GetAsync();
            if (!ledger.Entries.TryGetValue(advisoryId, out var entry) || !entry.Suppressed)
                return false; // Nothing recorded, or not currently suppressed - nothing to reverse.

            entry.Suppressed = false;
            entry.Status = LedgerStatus.Unsuppressed;
            entry.ContentHash = ""; // See doc comment above - withheld so AdvisoryFetcher re-delivers it.
            await settingsRepository.UpdateSetting(ledger);
            return true;
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
