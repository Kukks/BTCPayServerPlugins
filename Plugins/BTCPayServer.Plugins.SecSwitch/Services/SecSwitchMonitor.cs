using System;
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
    ILogger<SecSwitchMonitor> logger)
{
    /// <summary>
    /// A core update is dispatched at most once per window, whichever of an advisory or auto-update asks for it. The
    /// host command is not awaited to completion, so a failed update would otherwise never be retried.
    /// </summary>
    public static readonly TimeSpan CoreUpdateWindow = TimeSpan.FromHours(24);

    sealed record Evaluation(LedgerEntry? Entry, bool RestartNeeded, bool Executed);

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
        var entries = (await ledger.GetAsync()).Entries;
        var recorded = new List<LedgerEntry>();
        var restartNeeded = false;
        var coreUpdateRecent = CoreUpdateDispatchedRecently(entries.Values);
        var coreUpdateDispatched = false;

        foreach (var advisory in advisories)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                entries.TryGetValue(advisory.Id, out var existing);
                if (existing is { Suppressed: true })
                    continue;

                var evaluation = await EvaluateAsync(advisory, existing, state, settings, coreUpdateRecent);
                restartNeeded |= evaluation.RestartNeeded;
                if (evaluation.Entry is not { } entry)
                    continue;

                var dispatched = evaluation.Executed && IsCoreUpdateEntry(entry);
                coreUpdateRecent |= dispatched;
                coreUpdateDispatched |= dispatched;
                if (IsChange(existing, entry) || (evaluation.Executed && entry.Status == LedgerStatus.Acted))
                {
                    await ledger.RecordAsync(entry);
                    recorded.Add(entry);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "SecSwitch failed to process advisory {Id}; skipping it this poll", advisory.Id);
            }
        }

        var listed = advisories.Select(a => a.Id).Concat(unreadableIds.OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in entries.Values.Where(e => e is { LockedDown: true, Suppressed: false } && !listed.Contains(e.AdvisoryId)))
        {
            stale.LockedDown = false;
            stale.Status = LedgerStatus.Resolved;
            stale.Reason = "The advisory is no longer listed in the feed.";
            stale.RecordedAt = DateTimeOffset.UtcNow;
            await ledger.RecordAsync(stale);
            recorded.Add(stale);
        }

        return new ProcessResult(recorded, restartNeeded, coreUpdateDispatched);
    }

    /// <summary>Updates plugins (and BTCPay Server through the host integration) to newer releases, once per target version.</summary>
    public async Task<ProcessResult> AutoUpdateAsync(InstanceState state, CancellationToken ct)
    {
        var entries = (await ledger.GetAsync()).Entries;
        var recorded = new List<LedgerEntry>();
        var restartNeeded = false;

        foreach (var update in await sink.FindPluginUpdatesAsync(state.InstalledPlugins, ct))
        {
            var outcome = await UpdateOnceAsync(entries, update.Identifier, update.Available,
                SecSwitchAction.UpdatePlugin, () => executor.UpdatePluginAsync(update.Identifier, update.Available.ToString()), recorded);
            restartNeeded |= outcome?.RestartNeeded == true;
        }

        var coreUpdateDispatched = false;
        if (state.CanUpdateCore && state.CoreVersion is { } current && !CoreUpdateDispatchedRecently(entries.Values) &&
            await sink.GetLatestCoreVersionAsync(ct) is { } latest &&
            AdvisoryApplicability.Pad(latest) > AdvisoryApplicability.Pad(current))
        {
            var outcome = await UpdateOnceAsync(entries, AdvisoryApplicability.CoreIdentifier, latest,
                SecSwitchAction.UpdateCore, executor.UpdateCoreAsync, recorded);
            coreUpdateDispatched = outcome?.Succeeded == true;
        }

        return new ProcessResult(recorded, restartNeeded, coreUpdateDispatched);
    }

    /// <summary>
    /// True while a plugin change SecSwitch queued is still waiting for a restart. Derived from core's own pending-command
    /// file rather than remembered, so a restart that was skipped, or that did not happen, is requested again later.
    /// </summary>
    public static bool HasPendingChanges(IEnumerable<string> pendingPluginIdentifiers, IEnumerable<LedgerEntry> entries)
    {
        var ours = entries
            .Where(e => e.Status == LedgerStatus.Acted &&
                        e.Action is nameof(SecSwitchAction.DisablePlugin) or nameof(SecSwitchAction.UpdatePlugin))
            .Select(e => e.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pendingPluginIdentifiers.Any(ours.Contains);
    }

    async Task<Evaluation> EvaluateAsync(
        Advisory advisory, LedgerEntry? existing, InstanceState state, SecSwitchSettings settings, bool coreUpdateRecent)
    {
        var decision = PolicyResolver.Resolve(advisory, state, settings);
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
                return new Evaluation(entry, false, false);
            case SecSwitchAction.None:
                if (existing is not { LockedDown: true })
                    return new Evaluation(null, false, false);
                entry.Status = LedgerStatus.Resolved;
                return new Evaluation(entry, false, false);
            case SecSwitchAction.Notify:
                entry.Status = LedgerStatus.NeedsDecision;
                return new Evaluation(entry, false, false);
        }

        entry.LockedDown = decision.Action is SecSwitchAction.Lockdown or SecSwitchAction.UpdateCore;
        if (existing is { Status: LedgerStatus.Acted } && existing.Action == entry.Action && !CoreUpdateDue(existing))
            return new Evaluation(null, false, false);

        if (decision.Action == SecSwitchAction.UpdateCore && coreUpdateRecent)
        {
            // Recorded as the lockdown it is: an UpdateCore entry must only ever mean an update was really dispatched.
            entry.Action = nameof(SecSwitchAction.Lockdown);
            entry.Status = LedgerStatus.Acted;
            entry.Reason = $"{entry.Reason} A BTCPay Server update was already dispatched within the last day.".Trim();
            return new Evaluation(entry, false, false);
        }

        var outcome = await executor.ExecuteAsync(decision.Action, advisory);
        entry.Status = outcome.Succeeded ? LedgerStatus.Acted : LedgerStatus.NeedsAttention;
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
            Status = outcome.Succeeded ? LedgerStatus.Acted : LedgerStatus.NeedsAttention,
            Reason = outcome.Message,
            RecordedAt = DateTimeOffset.UtcNow
        };
        if (IsChange(existing, entry) || outcome.Succeeded)
        {
            await ledger.RecordAsync(entry);
            recorded.Add(entry);
        }
        return outcome;
    }

    static bool IsCoreUpdateEntry(LedgerEntry entry) =>
        entry.Action == nameof(SecSwitchAction.UpdateCore) && entry.Status == LedgerStatus.Acted;

    static bool CoreUpdateDue(LedgerEntry existing) =>
        IsCoreUpdateEntry(existing) && DateTimeOffset.UtcNow - existing.RecordedAt >= CoreUpdateWindow;

    static bool CoreUpdateDispatchedRecently(IEnumerable<LedgerEntry> entries) =>
        entries.Any(e => IsCoreUpdateEntry(e) && DateTimeOffset.UtcNow - e.RecordedAt < CoreUpdateWindow);

    static bool IsChange(LedgerEntry? existing, LedgerEntry entry) =>
        existing is null || existing.Status != entry.Status || existing.Action != entry.Action ||
        existing.Reason != entry.Reason || existing.LockedDown != entry.LockedDown;
}
