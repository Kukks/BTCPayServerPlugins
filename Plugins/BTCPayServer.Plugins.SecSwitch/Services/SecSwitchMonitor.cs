using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <param name="CoreUpdateDispatched">True only when a core update was actually started, not merely recorded.</param>
public sealed record ProcessResult(IReadOnlyList<LedgerEntry> Recorded, bool RestartNeeded, bool CoreUpdateDispatched = false,
    bool CoreUpdateMayBeRunning = false);

public sealed class SecSwitchMonitor(
    ActionExecutor executor,
    IActionSink sink,
    LedgerStore ledger,
    LockdownGate lockdown,
    ILogger<SecSwitchMonitor> logger)
{
    /// <summary>
    /// A core update is dispatched at most once per window, whichever of an advisory or auto-update asks for it. The
    /// host command is not awaited to completion, so a failed update would otherwise never be retried.
    /// </summary>
    public static readonly TimeSpan CoreUpdateWindow = TimeSpan.FromHours(24);
    readonly ConcurrentDictionary<string, LedgerEntry> _pendingAudit = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, LedgerEntry> _queuedPluginChanges = new(StringComparer.OrdinalIgnoreCase);

    sealed record Evaluation(LedgerEntry? Entry, bool RestartNeeded, bool Executed, bool CoreUpdateMayBeRunning = false);

    public IReadOnlyList<LedgerEntry> PendingAuditEntries => _pendingAudit.Values.ToList();

    public IReadOnlyList<LedgerEntry> GetPendingPluginChangeEntries(IEnumerable<string> pendingIdentifiers)
    {
        var pending = pendingIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in _queuedPluginChanges)
            if (!pending.Contains(change.Key))
                _queuedPluginChanges.TryRemove(change);
        return _queuedPluginChanges.Values.ToList();
    }

    public async Task RetryPendingAuditAsync()
    {
        foreach (var entry in PendingAuditEntries)
            await RecordQueuedChangeAsync(entry);
    }

    async Task RecordQueuedChangeAsync(LedgerEntry entry, bool newlyQueued = false)
    {
        if (newlyQueued)
            _queuedPluginChanges[entry.Identifier] = entry;
        try
        {
            await ledger.RecordQueuedOutcomeAsync(entry);
            if (_pendingAudit.TryGetValue(entry.AdvisoryId, out var pending) && pending.RecordedAt <= entry.RecordedAt)
                _pendingAudit.TryRemove(new KeyValuePair<string, LedgerEntry>(entry.AdvisoryId, pending));
        }
        catch (Exception e)
        {
            _pendingAudit.AddOrUpdate(entry.AdvisoryId, entry, (_, pending) => pending.RecordedAt > entry.RecordedAt ? pending : entry);
            logger.LogError(e, "SecSwitch will retry recording its queued change for {AdvisoryId}", entry.AdvisoryId);
        }
    }

    /// <summary>
    /// Evaluates every advisory against this instance on every poll. Nothing about "not applicable" is
    /// cached, so installing an affected plugin later is still caught; only advisories that applied
    /// (or could not be evaluated) are recorded. <paramref name="unreadableIds"/> are feed entries that
    /// could not be parsed: they still count as listed, so a typo never lifts a lockdown the way a deletion does.
    /// </summary>
    public async Task<ProcessResult> ProcessAsync(
        IReadOnlyList<Advisory> advisories, IEnumerable<string?> unreadableIds,
        InstanceState state, SecSwitchSettings settings, CancellationToken ct)
    {
        var snapshot = await ledger.GetAsync();
        var entries = snapshot.Entries;
        var recorded = new List<LedgerEntry>();
        var unreadable = unreadableIds.ToList();
        var queued = PendingChanges(snapshot);
        var changedBySecSwitch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restartNeeded = false;
        var coreUpdateRecent = CoreUpdateDispatchedRecently(snapshot.LastCoreUpdateAt);
        var coreUpdateDispatched = false;
        var coreUpdateMayBeRunning = false;
        var plannedDisables = PlannedDisables(advisories, state, settings, snapshot);
        var holdPluginChanges = LockdownGate.HasActiveLockdown(settings, snapshot) || settings is { Enabled: true, AutoApply: true } &&
            advisories.Any(a => IsCore(a.Identifier) && entries.GetValueOrDefault(a.Id) is not { Suppressed: true } &&
                                AdvisoryApplicability.IsApplicable(a, state, out _, out _));

        foreach (var advisory in advisories.OrderByDescending(a => IsCore(a.Identifier)))
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                entries.TryGetValue(advisory.Id, out var existing);
                if (existing is { Suppressed: true })
                    continue;

                var allowFix = unreadable.Count == 0 &&
                               (!Version.TryParse(advisory.FixedVersion, out var candidate) ||
                                IsSafeUpdate(advisories, state, advisory.Identifier, candidate, queued, plannedDisables));
                var evaluation = await EvaluateAsync(advisory, existing, state, settings, coreUpdateRecent,
                    allowFix, changedBySecSwitch.Contains(advisory.Identifier), holdPluginChanges, advisories, plannedDisables, ct);
                restartNeeded |= evaluation.RestartNeeded;
                if (evaluation.RestartNeeded)
                {
                    queued.Add(advisory.Identifier);
                    changedBySecSwitch.Add(advisory.Identifier);
                }
                if (evaluation.Entry is not { } entry)
                    continue;

                var dispatched = evaluation.Executed && IsCoreUpdateEntry(entry);
                coreUpdateRecent |= dispatched || evaluation.CoreUpdateMayBeRunning;
                coreUpdateDispatched |= dispatched;
                coreUpdateMayBeRunning |= evaluation.CoreUpdateMayBeRunning;
                if (coreUpdateRecent)
                    queued.Add(AdvisoryApplicability.CoreIdentifier);
                if (IsChange(existing, entry) || (evaluation.Executed && entry.Status == LedgerStatus.Acted))
                {
                    if (await RecordForPolicyAsync(settings, entry, evaluation.RestartNeeded))
                        recorded.Add(entry);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "SecSwitch failed to process advisory {Id}; skipping it this poll", advisory.Id);
            }
        }

        var listed = advisories.Select(a => a.Id).Concat(unreadable.OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!ct.IsCancellationRequested && !unreadable.Any(string.IsNullOrWhiteSpace))
        {
            foreach (var stale in entries.Values.Where(e => !e.Suppressed && !listed.Contains(e.AdvisoryId) &&
                (e.LockedDown || e.Status == LedgerStatus.NeedsDecision && !e.AdvisoryId.StartsWith("autoupdate:", StringComparison.OrdinalIgnoreCase))))
            {
                try
                {
                    stale.LockedDown = false;
                    stale.Status = LedgerStatus.Resolved;
                    stale.Reason = "The advisory is no longer listed in the feed.";
                    stale.RecordedAt = DateTimeOffset.UtcNow;
                    if (await RecordForPolicyAsync(settings, stale, pluginChangeQueued: false))
                        recorded.Add(stale);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "SecSwitch could not record the withdrawal of advisory {Id}", stale.AdvisoryId);
                }
            }
        }

        return new ProcessResult(recorded, restartNeeded, coreUpdateDispatched, coreUpdateMayBeRunning);
    }

    /// <summary>Updates plugins (and BTCPay Server through the host integration) to newer releases, once per target version.</summary>
    public async Task<ProcessResult> AutoUpdateAsync(
        InstanceState state, SecSwitchSettings settings, IReadOnlyList<Advisory> advisories, CancellationToken ct)
    {
        var snapshot = await ledger.GetAsync();
        if (LockdownGate.HasActiveLockdown(settings, snapshot))
            return new ProcessResult([], false);
        var entries = snapshot.Entries;
        var recorded = new List<LedgerEntry>();
        var restartNeeded = false;

        var pending = PendingChanges(snapshot);
        var optedIn = state.InstalledPlugins
            .Where(p => settings.IsPluginAutoUpdateEnabled(p.Key) && !pending.Contains(p.Key) &&
                        !HasApplicableAdvisory(advisories, state, p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var updates = optedIn.Count == 0 ? [] : await sink.FindPluginUpdatesAsync(optedIn, ct);
        foreach (var update in updates)
        {
            if (ct.IsCancellationRequested)
                break;
            if (!optedIn.ContainsKey(update.Identifier) || pending.Contains(update.Identifier) ||
                !IsSafeUpdate(advisories, state, update.Identifier, update.Available, pending))
                continue;
            var outcome = await UpdateOnceAsync(entries, update.Identifier, update.Available,
                SecSwitchAction.UpdatePlugin, id => executor.UpdatePluginAsync(update.Identifier, update.Available.ToString(),
                    AuthorizePluginUpdate(settings, advisories, state, id, update.Identifier, update.Available, [], ct)), recorded);
            if (outcome?.RestartNeeded == true)
            {
                pending.Add(update.Identifier);
                restartNeeded = true;
            }
        }

        var coreUpdateDispatched = false;
        var coreUpdateMayBeRunning = false;
        if (!ct.IsCancellationRequested && settings.AutoUpdateCore && state.CanUpdateCore && state.CoreVersion is { } current &&
            !HasApplicableAdvisory(advisories, state, AdvisoryApplicability.CoreIdentifier) &&
            !CoreUpdateDispatchedRecently(snapshot.LastCoreUpdateAt) &&
            await sink.GetLatestCoreVersionAsync(ct) is { } latest &&
            AdvisoryApplicability.Pad(latest) > AdvisoryApplicability.Pad(current) &&
            IsSafeUpdate(advisories, state, AdvisoryApplicability.CoreIdentifier, latest, pending) &&
            !ct.IsCancellationRequested)
        {
            var outcome = await UpdateOnceAsync(entries, AdvisoryApplicability.CoreIdentifier, latest,
                SecSwitchAction.UpdateCore, id => RunCoreUpdateAsync(settings, id, state, advisories, latest, [], ct), recorded);
            coreUpdateDispatched = outcome?.Succeeded == true;
            coreUpdateMayBeRunning = outcome?.CoreUpdateMayBeRunning == true;
        }

        return new ProcessResult(recorded, restartNeeded, coreUpdateDispatched, coreUpdateMayBeRunning);
    }

    static bool PolicyMatches(SecSwitchSettings expected, SecSwitchSettings current) =>
        current.Enabled && expected.PolicyRevision == current.PolicyRevision;

    Task<bool> RecordForPolicyAsync(SecSwitchSettings expected, LedgerEntry entry, bool pluginChangeQueued) =>
        ledger.WithPolicyAsync(async current =>
        {
            // Retain a plugin change already queued under the previous policy so it can be applied.
            if (!PolicyMatches(expected, current) && !pluginChangeQueued)
                return false;
            entry.LockedDown &= current.AutoApply;
            if (pluginChangeQueued)
                await RecordQueuedChangeAsync(entry, newlyQueued: true);
            else
                await ledger.RecordAsync(entry);
            if (entry is { LockedDown: true, Suppressed: false })
                await lockdown.ActivateAsync();
            else if (IsCore(entry.Identifier))
                await lockdown.RefreshAsync();
            return true;
        });

    Func<Func<Task<bool>>, Task<bool>> AuthorizePluginUpdate(SecSwitchSettings expected,
        IReadOnlyList<Advisory> advisories, InstanceState state, string id, string identifier, Version version,
        IReadOnlyCollection<string> plannedDisables, CancellationToken ct) =>
        commit => ledger.WithPolicyAsync(async current =>
        {
            if (ct.IsCancellationRequested || !PolicyMatches(expected, current) ||
                !current.IsPluginAutoUpdateEnabled(identifier))
                return false;
            var snapshot = await ledger.GetAsync();
            if (IsSuppressed(snapshot, id) || LockdownGate.HasActiveLockdown(current, snapshot) ||
                !IsSafeUpdate(advisories, state, identifier, version, PendingChanges(snapshot), plannedDisables))
                return false;
            return await commit();
        });

    public Task<ActionOutcome> RecoverWithCoreUpdateAsync(CancellationToken ct) =>
        ledger.WithPolicyAsync(async current =>
        {
            var snapshot = await ledger.GetAsync();
            if (!LockdownGate.HasActiveLockdown(current, snapshot))
                return new ActionOutcome(false, "BTCPay Server is not in a SecSwitch lockdown.");
            return await DispatchCoreUpdateAsync(snapshot.LastCoreUpdateAt, ct);
        });

    Task<ActionOutcome> RunCoreUpdateAsync(SecSwitchSettings expected, string id, InstanceState state,
        IReadOnlyList<Advisory> advisories, Version candidate, IReadOnlyCollection<string> plannedDisables,
        CancellationToken ct, LedgerEntry? advisory = null) =>
        ledger.WithPolicyAsync(async current =>
        {
            if (ct.IsCancellationRequested || !PolicyMatches(expected, current) || !current.AutoUpdateCore)
                return new ActionOutcome(false, "The automatic core update was deferred.");

            var snapshot = await ledger.GetAsync();
            if (IsSuppressed(snapshot, id) || advisory is null && LockdownGate.HasActiveLockdown(current, snapshot))
                return new ActionOutcome(false, "The automatic core update was deferred.");

            if (advisory is { LockedDown: true })
            {
                // The updater can restart the process before its outcome is recorded.
                var lockEntry = new LedgerEntry
                {
                    AdvisoryId = advisory.AdvisoryId,
                    Title = advisory.Title,
                    Identifier = advisory.Identifier,
                    Severity = advisory.Severity,
                    Status = LedgerStatus.Acted,
                    Action = nameof(SecSwitchAction.Lockdown),
                    Reason = advisory.Reason,
                    RecordedAt = advisory.RecordedAt,
                    LockedDown = true
                };
                await ledger.RecordAsync(lockEntry);
                if (lockEntry.Suppressed)
                    return new ActionOutcome(false, "Could not activate lockdown before updating BTCPay Server.");
                await lockdown.ActivateAsync();
            }

            if (!IsSafeUpdate(advisories, state, AdvisoryApplicability.CoreIdentifier, candidate, PendingChanges(snapshot), plannedDisables))
                return new ActionOutcome(false, "The core update was deferred because a related plugin change might activate an advisory.");
            return await DispatchCoreUpdateAsync(snapshot.LastCoreUpdateAt, ct);
        });

    async Task<ActionOutcome> DispatchCoreUpdateAsync(DateTimeOffset? lastDispatch, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new ActionOutcome(false, "The core update was cancelled before dispatch.");
        if (!sink.CanUpdateCore)
            return new ActionOutcome(false, "This host does not offer an update command. Update BTCPay Server on the deployment host.");
        if (CoreUpdateDispatchedRecently(lastDispatch))
            return new ActionOutcome(false, "A BTCPay Server update was already dispatched within the last day.");
        var reservation = DateTimeOffset.UtcNow;
        await ledger.RecordCoreUpdateAsync(reservation);
        var outcome = ct.IsCancellationRequested
            ? new ActionOutcome(false, "The core update was cancelled before dispatch.")
            : await executor.UpdateCoreAsync();
        if (!outcome.Succeeded && !outcome.CoreUpdateMayBeRunning)
        {
            try
            {
                await ledger.ReleaseCoreUpdateAsync(reservation, lastDispatch);
            }
            catch (Exception e)
            {
                logger.LogError(e, "SecSwitch could not release the failed core update reservation");
            }
        }
        return outcome;
    }

    HashSet<string> PendingChanges(SecSwitchLedger snapshot)
    {
        var pending = sink.PendingPluginIdentifiers().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (CoreUpdateDispatchedRecently(snapshot.LastCoreUpdateAt))
            pending.Add(AdvisoryApplicability.CoreIdentifier);
        return pending;
    }

    static bool IsSuppressed(SecSwitchLedger snapshot, string id) =>
        snapshot.Entries.GetValueOrDefault(id) is { Suppressed: true };

    static bool IsCore(string identifier) => SameIdentifier(identifier, AdvisoryApplicability.CoreIdentifier);

    static bool SameIdentifier(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static bool HasApplicableAdvisory(IEnumerable<Advisory> advisories, InstanceState state, string identifier) =>
        advisories.Any(a => SameIdentifier(a.Identifier, identifier) && AdvisoryApplicability.IsApplicable(a, state, out _, out _));

    static HashSet<string> PlannedDisables(
        IEnumerable<Advisory> advisories, InstanceState state, SecSwitchSettings settings, SecSwitchLedger snapshot) =>
        settings is { Enabled: true, AutoApply: true }
            ? advisories.Where(a => !IsCore(a.Identifier) && !IsSuppressed(snapshot, a.Id) &&
                                    AdvisoryApplicability.IsApplicable(a, state, out _, out _))
                .Select(a => a.Identifier).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static bool IsSafeUpdate(
        IEnumerable<Advisory> advisories, InstanceState state, string identifier, Version candidate, ISet<string> pending,
        IReadOnlyCollection<string>? plannedDisables = null)
    {
        foreach (var advisory in advisories)
        {
            var updatingTarget = SameIdentifier(advisory.Identifier, identifier);
            if (!updatingTarget && !advisory.Dependencies.Any(d => SameIdentifier(d.Identifier, identifier)))
                continue;
            if (AdvisoryApplicability.IsApplicableAfterUpdate(advisory, state, identifier, candidate))
                return false;
            if (advisory.Dependencies.Length != 0 &&
                ((!updatingTarget && pending.Contains(advisory.Identifier)) ||
                 advisory.Dependencies.Any(d => !SameIdentifier(d.Identifier, identifier) && pending.Contains(d.Identifier))))
                return false;
            if (plannedDisables is not { Count: > 0 })
                continue;
            var absent = advisory.Dependencies
                .Where(d => plannedDisables.Contains(d.Identifier, StringComparer.OrdinalIgnoreCase) &&
                            AdvisoryApplicability.TryParseDependencyCondition(d.Condition, out var condition) &&
                            condition is VersionCondition.Not)
                .Select(d => d.Identifier)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (absent.Count == 0)
                continue;
            var afterDisables = state with
            {
                InstalledPlugins = state.InstalledPlugins.Where(p => !absent.Contains(p.Key))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)
            };
            if (AdvisoryApplicability.IsApplicableAfterUpdate(advisory, afterDisables, identifier, candidate))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True while a plugin change SecSwitch queued is still waiting for a restart. Derived from core's own pending-command
    /// file rather than remembered, so a restart that was skipped, or that did not happen, is requested again later.
    /// Only entries recorded since this process started count: core applies the command file at startup, so an older
    /// entry cannot still be pending, and its ledger record (which never expires) must not claim an admin's own
    /// queued change for the same plugin.
    /// </summary>
    public static bool HasPendingChanges(
        IEnumerable<string> pendingPluginIdentifiers, IEnumerable<LedgerEntry> entries, DateTimeOffset processStartedAt)
    {
        var ours = entries
            .Where(e => e.Status == LedgerStatus.Acted && e.RecordedAt >= processStartedAt &&
                        e.Action is nameof(SecSwitchAction.DisablePlugin) or nameof(SecSwitchAction.UpdatePlugin))
            .Select(e => e.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pendingPluginIdentifiers.Any(ours.Contains);
    }

    async Task<Evaluation> EvaluateAsync(
        Advisory advisory, LedgerEntry? existing, InstanceState state, SecSwitchSettings settings, bool coreUpdateRecent,
        bool allowFix, bool pluginChangeQueued, bool holdPluginChanges, IReadOnlyList<Advisory> advisories,
        IReadOnlyCollection<string> plannedDisables, CancellationToken ct)
    {
        var decision = PolicyResolver.Resolve(advisory, state, settings, allowFix);
        if (holdPluginChanges && decision.Action is SecSwitchAction.UpdatePlugin or SecSwitchAction.DisablePlugin)
        {
            // Preserve recorded ownership of a plugin change already queued before lockdown.
            if (existing is { Status: LedgerStatus.Acted, Action: nameof(SecSwitchAction.UpdatePlugin) or nameof(SecSwitchAction.DisablePlugin) })
                return new Evaluation(null, false, false);
            decision = new PolicyDecision(SecSwitchAction.Notify, $"{decision.Reason} Plugin changes are paused during BTCPay Server lockdown.");
        }
        var entry = new LedgerEntry
        {
            AdvisoryId = advisory.Id,
            Title = ActionExecutor.Sanitize(advisory.Title),
            Identifier = ActionExecutor.Sanitize(advisory.Identifier),
            Severity = advisory.Severity.ToString(),
            Action = decision.Action.ToString(),
            Reason = ActionExecutor.Sanitize(decision.Reason),
            RecordedAt = DateTimeOffset.UtcNow
        };

        switch (decision.Action)
        {
            case SecSwitchAction.None when decision.Reason.Contains(AdvisoryApplicability.IndeterminateVersionPhrase, StringComparison.OrdinalIgnoreCase):
                entry.Status = LedgerStatus.NeedsAttention;
                entry.LockedDown = settings.AutoApply && existing is { LockedDown: true };
                return new Evaluation(entry, false, false);
            case SecSwitchAction.None:
                if (existing is null || existing.Status == LedgerStatus.Resolved)
                    return new Evaluation(null, false, false);
                entry.Status = LedgerStatus.Resolved;
                return new Evaluation(entry, false, false);
            case SecSwitchAction.Notify:
                entry.Status = LedgerStatus.NeedsDecision;
                return new Evaluation(entry, false, false);
        }

        entry.LockedDown = settings.AutoApply && decision.Action is SecSwitchAction.Lockdown or SecSwitchAction.UpdateCore;
        if (existing is { Status: LedgerStatus.Acted } && existing.Action == entry.Action &&
            existing.LockedDown == entry.LockedDown && !CoreUpdateDue(existing))
            return new Evaluation(null, false, false);

        if (pluginChangeQueued)
        {
            entry.Status = LedgerStatus.NeedsAttention;
            entry.Reason = $"{entry.Reason} A plugin change was already queued by SecSwitch. The advisory will be checked again.";
            return new Evaluation(entry, false, false);
        }

        if (decision.Action == SecSwitchAction.UpdateCore && coreUpdateRecent)
        {
            entry.Action = entry.LockedDown ? nameof(SecSwitchAction.Lockdown) : nameof(SecSwitchAction.Notify);
            entry.Status = entry.LockedDown ? LedgerStatus.Acted : LedgerStatus.NeedsDecision;
            entry.Reason = $"{entry.Reason} A BTCPay Server update was already dispatched within the last day.".Trim();
            return new Evaluation(entry, false, false);
        }

        var commitUpdate = Version.TryParse(advisory.FixedVersion, out var candidate)
            ? AuthorizePluginUpdate(settings, advisories, state, advisory.Id, advisory.Identifier, candidate, plannedDisables, ct)
            : null;
        Task<ActionOutcome> CommitDisable(Func<ActionOutcome> commit) => ledger.WithPolicyAsync(async current =>
        {
            if (ct.IsCancellationRequested || !PolicyMatches(settings, current) || !current.AutoApply)
                return new ActionOutcome(false, "The plugin disable was deferred because the kill switch policy changed.");
            var snapshot = await ledger.GetAsync();
            if (IsSuppressed(snapshot, advisory.Id) || LockdownGate.HasActiveLockdown(current, snapshot))
                return new ActionOutcome(false, "The plugin disable was deferred because the kill switch policy changed.");
            if (allowFix && candidate is not null &&
                AdvisoryApplicability.IsApplicable(advisory, state, out var installed, out _) &&
                AdvisoryApplicability.HasUsableFix(advisory, installed!) &&
                IsSafeUpdate(advisories, state, advisory.Identifier, candidate, PendingChanges(snapshot), plannedDisables) &&
                sink.HasVerifiedQueuedUpdate(advisory.Identifier, advisory.FixedVersion!))
                return new ActionOutcome(true, "An administrator already queued the fixed version.", AlreadyQueued: true);
            return commit();
        });

        var outcome = decision.Action == SecSwitchAction.UpdateCore
            ? await RunCoreUpdateAsync(settings, advisory.Id, state, advisories, candidate!, plannedDisables, ct, entry)
            : await executor.ExecuteAsync(decision.Action, advisory, settings.AutoApply, commitUpdate, CommitDisable);
        entry.Status = outcome.AlreadyQueued ? LedgerStatus.NeedsDecision :
            outcome.Succeeded ? LedgerStatus.Acted : LedgerStatus.NeedsAttention;
        entry.Reason = $"{entry.Reason} {outcome.Message}".Trim();
        if (!outcome.Succeeded)
            logger.LogError("SecSwitch could not apply {Action} for advisory {Id}: {Outcome}", decision.Action, advisory.Id, outcome.Message);
        return new Evaluation(entry, outcome.RestartNeeded, true, outcome.CoreUpdateMayBeRunning);
    }

    async Task<ActionOutcome?> UpdateOnceAsync(
        IReadOnlyDictionary<string, LedgerEntry> entries, string identifier, Version version,
        SecSwitchAction action, Func<string, Task<ActionOutcome>> run, List<LedgerEntry> recorded)
    {
        var id = $"autoupdate:{identifier}@{version}";
        entries.TryGetValue(id, out var existing);
        if (existing is { Suppressed: true } || (existing is { Status: LedgerStatus.Acted } && !CoreUpdateDue(existing)))
            return null;

        var outcome = await run(id);
        var entry = new LedgerEntry
        {
            AdvisoryId = id,
            Title = ActionExecutor.Sanitize($"Update available: {identifier} {version}"),
            Identifier = ActionExecutor.Sanitize(identifier),
            Severity = "Update",
            Action = action.ToString(),
            Status = outcome.AlreadyQueued ? LedgerStatus.NeedsDecision :
                outcome.Succeeded ? LedgerStatus.Acted : LedgerStatus.NeedsAttention,
            Reason = outcome.Message,
            RecordedAt = DateTimeOffset.UtcNow
        };
        if (IsChange(existing, entry) || outcome.Succeeded)
        {
            if (outcome.RestartNeeded)
                await RecordQueuedChangeAsync(entry, newlyQueued: true);
            else
            {
                try
                {
                    await ledger.RecordAsync(entry);
                }
                catch (Exception e) when (action == SecSwitchAction.UpdateCore && (outcome.Succeeded || outcome.CoreUpdateMayBeRunning))
                {
                    logger.LogError(e, "SecSwitch could not record the audit outcome of a dispatched core update");
                }
            }
            recorded.Add(entry);
        }
        return outcome;
    }

    static bool IsCoreUpdateEntry(LedgerEntry entry) =>
        entry.Action == nameof(SecSwitchAction.UpdateCore) && entry.Status == LedgerStatus.Acted;

    static bool CoreUpdateDue(LedgerEntry existing) =>
        IsCoreUpdateEntry(existing) && DateTimeOffset.UtcNow - existing.RecordedAt >= CoreUpdateWindow;

    static bool CoreUpdateDispatchedRecently(DateTimeOffset? lastDispatch) =>
        lastDispatch > DateTimeOffset.UtcNow - CoreUpdateWindow;

    static bool IsChange(LedgerEntry? existing, LedgerEntry entry) =>
        existing is null || existing.Status != entry.Status || existing.Action != entry.Action ||
        existing.Reason != entry.Reason || existing.LockedDown != entry.LockedDown ||
        existing.Identifier != entry.Identifier || existing.Title != entry.Title || existing.Severity != entry.Severity;
}
