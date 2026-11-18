using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <param name="CoreUpdateDispatched">True only when a core update was actually started, not merely recorded.</param>
public sealed record ProcessResult(IReadOnlyList<LedgerEntry> Recorded, bool RestartNeeded, bool CoreUpdateDispatched = false);

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
    long _unrecordedCoreDispatchTicks;

    sealed record Evaluation(LedgerEntry? Entry, bool RestartNeeded, bool Executed);

    public IReadOnlyList<LedgerEntry> PendingAuditEntries => _pendingAudit.Values.ToList();

    public async Task RetryPendingAuditAsync()
    {
        foreach (var entry in PendingAuditEntries)
            await RecordQueuedChangeAsync(entry);
        var ticks = Interlocked.Read(ref _unrecordedCoreDispatchTicks);
        if (ticks != 0)
            await RecordCoreDispatchAsync(new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    async Task RecordQueuedChangeAsync(LedgerEntry entry)
    {
        try
        {
            await ledger.RecordAsync(entry);
            _pendingAudit.TryRemove(entry.AdvisoryId, out _);
        }
        catch (Exception e)
        {
            _pendingAudit[entry.AdvisoryId] = entry;
            logger.LogError(e, "SecSwitch will retry recording its queued change for {AdvisoryId}", entry.AdvisoryId);
        }
    }

    async Task RecordCoreDispatchAsync(DateTimeOffset at)
    {
        try
        {
            await ledger.RecordCoreUpdateAsync(at);
            Interlocked.CompareExchange(ref _unrecordedCoreDispatchTicks, 0, at.UtcTicks);
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref _unrecordedCoreDispatchTicks, at.UtcTicks);
            logger.LogError(e, "SecSwitch will retry recording the dispatched core update");
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
        var queued = new HashSet<string>(sink.PendingPluginIdentifiers(), StringComparer.OrdinalIgnoreCase);
        var changedBySecSwitch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restartNeeded = false;
        var coreUpdateRecent = CoreUpdateDispatchedRecently(snapshot.LastCoreUpdateAt);
        var coreUpdateDispatched = false;
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
                                IsSafeUpdate(advisories, state, advisory.Identifier, candidate, queued));
                var evaluation = await EvaluateAsync(advisory, existing, state, settings, coreUpdateRecent,
                    allowFix, changedBySecSwitch.Contains(advisory.Identifier), holdPluginChanges, advisories, ct);
                restartNeeded |= evaluation.RestartNeeded;
                if (evaluation.RestartNeeded)
                {
                    queued.Add(advisory.Identifier);
                    changedBySecSwitch.Add(advisory.Identifier);
                }
                if (evaluation.Entry is not { } entry)
                    continue;

                var dispatched = evaluation.Executed && IsCoreUpdateEntry(entry);
                coreUpdateRecent |= dispatched;
                coreUpdateDispatched |= dispatched;
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
                stale.LockedDown = false;
                stale.Status = LedgerStatus.Resolved;
                stale.Reason = "The advisory is no longer listed in the feed.";
                stale.RecordedAt = DateTimeOffset.UtcNow;
                if (await RecordForPolicyAsync(settings, stale, pluginChangeQueued: false))
                    recorded.Add(stale);
            }
        }

        return new ProcessResult(recorded, restartNeeded, coreUpdateDispatched);
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

        var pending = new HashSet<string>(sink.PendingPluginIdentifiers(), StringComparer.OrdinalIgnoreCase);
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
                SecSwitchAction.UpdatePlugin, () => executor.UpdatePluginAsync(update.Identifier, update.Available.ToString(),
                    AuthorizePluginUpdate(settings, advisories, state, update.Identifier, update.Available, ct)), recorded);
            if (outcome?.RestartNeeded == true)
            {
                pending.Add(update.Identifier);
                restartNeeded = true;
            }
        }

        var coreUpdateDispatched = false;
        if (!ct.IsCancellationRequested && settings.AutoUpdateCore && state.CanUpdateCore && state.CoreVersion is { } current &&
            !HasApplicableAdvisory(advisories, state, AdvisoryApplicability.CoreIdentifier) &&
            !CoreUpdateDispatchedRecently(snapshot.LastCoreUpdateAt) &&
            await sink.GetLatestCoreVersionAsync(ct) is { } latest &&
            AdvisoryApplicability.Pad(latest) > AdvisoryApplicability.Pad(current) &&
            IsSafeUpdate(advisories, state, AdvisoryApplicability.CoreIdentifier, latest, pending) &&
            !ct.IsCancellationRequested)
        {
            var outcome = await UpdateOnceAsync(entries, AdvisoryApplicability.CoreIdentifier, latest,
                SecSwitchAction.UpdateCore, () => RunCoreUpdateAsync(settings, ct), recorded);
            coreUpdateDispatched = outcome?.Succeeded == true;
        }

        return new ProcessResult(recorded, restartNeeded, coreUpdateDispatched);
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
                await RecordQueuedChangeAsync(entry);
            else
                await ledger.RecordAsync(entry);
            if (IsCore(entry.Identifier))
                await lockdown.RefreshAsync();
            return true;
        });

    Func<Func<Task<bool>>, Task<bool>> AuthorizePluginUpdate(SecSwitchSettings expected,
        IReadOnlyList<Advisory> advisories, InstanceState state, string identifier, Version version, CancellationToken ct) =>
        commit => ledger.WithPolicyAsync(async current =>
        {
            if (ct.IsCancellationRequested || !PolicyMatches(expected, current) ||
                !current.IsPluginAutoUpdateEnabled(identifier) ||
                LockdownGate.HasActiveLockdown(current, await ledger.GetAsync()) ||
                !IsSafeUpdate(advisories, state, identifier, version,
                    sink.PendingPluginIdentifiers().ToHashSet(StringComparer.OrdinalIgnoreCase)))
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

    Task<ActionOutcome> RunCoreUpdateAsync(SecSwitchSettings expected, CancellationToken ct, LedgerEntry? advisory = null) =>
        ledger.WithPolicyAsync(async current =>
        {
            if (ct.IsCancellationRequested || !PolicyMatches(expected, current) || !current.AutoUpdateCore)
                return new ActionOutcome(false, "The automatic core update was deferred.");

            var snapshot = await ledger.GetAsync();
            if (advisory is null && LockdownGate.HasActiveLockdown(current, snapshot) ||
                advisory is not null && snapshot.Entries.GetValueOrDefault(advisory.AdvisoryId) is { Suppressed: true })
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
                await lockdown.RefreshAsync();
                if (lockEntry.Suppressed || !await lockdown.IsActiveAsync())
                    return new ActionOutcome(false, "Could not activate lockdown before updating BTCPay Server.");
            }

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
        var outcome = await executor.UpdateCoreAsync();
        if (outcome.Succeeded)
            await RecordCoreDispatchAsync(DateTimeOffset.UtcNow);
        return outcome;
    }

    static bool IsCore(string identifier) => SameIdentifier(identifier, AdvisoryApplicability.CoreIdentifier);

    static bool SameIdentifier(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static bool HasApplicableAdvisory(IEnumerable<Advisory> advisories, InstanceState state, string identifier) =>
        advisories.Any(a => SameIdentifier(a.Identifier, identifier) && AdvisoryApplicability.IsApplicable(a, state, out _, out _));

    static bool IsSafeUpdate(
        IEnumerable<Advisory> advisories, InstanceState state, string identifier, Version candidate, ISet<string> pending)
    {
        foreach (var advisory in advisories)
        {
            if (AdvisoryApplicability.IsApplicableAfterUpdate(advisory, state, identifier, candidate))
                return false;
            // A pending command may change a related component on the same restart.
            var updatingTarget = SameIdentifier(advisory.Identifier, identifier);
            if (advisory.Dependencies.Length != 0 &&
                (updatingTarget || advisory.Dependencies.Any(d => SameIdentifier(d.Identifier, identifier))) &&
                ((!updatingTarget && pending.Contains(advisory.Identifier)) ||
                 advisory.Dependencies.Any(d => !SameIdentifier(d.Identifier, identifier) && pending.Contains(d.Identifier))))
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
        bool allowFix, bool pluginChangeQueued, bool holdPluginChanges, IReadOnlyList<Advisory> advisories, CancellationToken ct)
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
            ? AuthorizePluginUpdate(settings, advisories, state, advisory.Identifier, candidate, ct)
            : null;
        Task<ActionOutcome> CommitDisable(Func<ActionOutcome> commit) => ledger.WithPolicyAsync(async current =>
        {
            if (ct.IsCancellationRequested || !PolicyMatches(settings, current) || !current.AutoApply ||
                LockdownGate.HasActiveLockdown(current, await ledger.GetAsync()))
                return new ActionOutcome(false, "The plugin disable was deferred because the kill switch policy changed.");
            if (allowFix && candidate is not null &&
                AdvisoryApplicability.IsApplicable(advisory, state, out var installed, out _) &&
                AdvisoryApplicability.HasUsableFix(advisory, installed!) &&
                IsSafeUpdate(advisories, state, advisory.Identifier, candidate,
                    sink.PendingPluginIdentifiers().ToHashSet(StringComparer.OrdinalIgnoreCase)) &&
                sink.HasVerifiedQueuedUpdate(advisory.Identifier, advisory.FixedVersion!))
                return new ActionOutcome(true, "An administrator already queued the fixed version.", AlreadyQueued: true);
            return commit();
        });

        var outcome = decision.Action == SecSwitchAction.UpdateCore
            ? await RunCoreUpdateAsync(settings, ct, entry)
            : await executor.ExecuteAsync(decision.Action, advisory, settings.AutoApply, commitUpdate, CommitDisable);
        entry.Status = outcome.AlreadyQueued ? LedgerStatus.NeedsDecision :
            outcome.Succeeded ? LedgerStatus.Acted : LedgerStatus.NeedsAttention;
        entry.Reason = $"{entry.Reason} {outcome.Message}".Trim();
        if (!outcome.Succeeded)
            logger.LogError("SecSwitch could not apply {Action} for advisory {Id}: {Outcome}", decision.Action, advisory.Id, outcome.Message);
        return new Evaluation(entry, outcome.RestartNeeded, true);
    }

    async Task<ActionOutcome?> UpdateOnceAsync(
        IReadOnlyDictionary<string, LedgerEntry> entries, string identifier, Version version,
        SecSwitchAction action, Func<Task<ActionOutcome>> run, List<LedgerEntry> recorded)
    {
        var id = $"autoupdate:{identifier}@{version}";
        entries.TryGetValue(id, out var existing);
        if (existing is { Suppressed: true } || (existing is { Status: LedgerStatus.Acted } && !CoreUpdateDue(existing)))
            return null;

        var outcome = await run();
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
                await RecordQueuedChangeAsync(entry);
            else
            {
                try
                {
                    await ledger.RecordAsync(entry);
                }
                catch (Exception e) when (action == SecSwitchAction.UpdateCore && outcome.Succeeded)
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

    bool CoreUpdateDispatchedRecently(DateTimeOffset? lastDispatch)
    {
        var inMemoryTicks = Interlocked.Read(ref _unrecordedCoreDispatchTicks);
        var cutoff = DateTimeOffset.UtcNow - CoreUpdateWindow;
        return lastDispatch > cutoff || inMemoryTicks > cutoff.UtcTicks;
    }

    static bool IsChange(LedgerEntry? existing, LedgerEntry entry) =>
        existing is null || existing.Status != entry.Status || existing.Action != entry.Action ||
        existing.Reason != entry.Reason || existing.LockedDown != entry.LockedDown ||
        existing.Identifier != entry.Identifier || existing.Title != entry.Title || existing.Severity != entry.Severity;
}
