using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// The orchestration layer: takes advisories already downloaded by <see cref="AdvisoryFetcher"/>,
/// verifies their GPG quorum, resolves policy, executes the resulting action, and records the
/// outcome in the ledger. This is where every other Task 2-10 component is wired together, so a
/// wrong wiring here would silently disarm the whole kill switch without any single component
/// itself being at fault.
///
/// <see cref="ProcessAsync"/> NEVER THROWS. It iterates over attacker-influenced advisories - both
/// the payload bytes and the armored signatures ultimately come from a network feed (see
/// <see cref="AdvisoryFetcher"/>'s own class doc comment) - and every component it calls
/// (<see cref="AdvisoryParser"/>, <see cref="AdvisoryVerifier"/>, <see cref="PolicyResolver"/>,
/// <see cref="ActionExecutor"/>, <see cref="LedgerStore"/>) was independently hardened to fail
/// closed rather than throw, for exactly this reason: one malformed or hostile advisory must never
/// abort the sweep over every other advisory in the same batch. That hardening is not undone here.
/// A per-item try/catch is kept anyway as a structural backstop - mirroring the same "backstop, not
/// the only line of defence" posture <see cref="AdvisoryFetcher.FetchAsync(string,ISet{string},System.Threading.CancellationToken)"/>
/// itself documents - and a second, outer try/catch protects the loop-control code that runs
/// before any single advisory is reached (building the trusted-key lookup from admin-supplied
/// settings, which are configuration, not attacker content, but still not something a single
/// malformed entry should be able to turn into a total processing failure).
///
/// Verification always precedes policy: <see cref="PolicyResolver.Resolve"/> and
/// <see cref="ActionExecutor.ExecuteAsync"/> are only ever reached for an advisory whose GPG quorum
/// already checked out - nothing here resolves or executes anything for an advisory that failed
/// quorum.
/// </summary>
public sealed class SecSwitchMonitor(
    ActionExecutor executor,
    LedgerStore ledger,
    ILogger<SecSwitchMonitor> logger) : IPeriodicTask
{
    public async Task<IReadOnlyList<LedgerEntry>> ProcessAsync(
        IReadOnlyList<FetchedAdvisory> fetched,
        InstanceState state,
        SecSwitchSettings settings,
        CancellationToken ct)
    {
        var recorded = new List<LedgerEntry>();
        if (fetched is null)
            return recorded; // Nothing to process - a liveness gap, not an error (matches
                              // AdvisoryFetcher's own "unusable input -> empty result" contract).

        try
        {
            // Built once, defensively, outside the per-advisory loop: settings.TrustedKeys is
            // admin-supplied configuration, but a naive ToDictionary over it throws
            // ArgumentException the moment two entries share a fingerprint (case-insensitively) -
            // which would abort the ENTIRE sweep, not just one advisory. Mirrors
            // TrustStore.TryApplyRotation's own defensive dictionary-building loop: null/blank
            // entries are skipped, and a duplicate fingerprint is last-write-wins, silently, rather
            // than thrown.
            var trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var quorumThreshold = 0;
            if (settings is not null)
            {
                quorumThreshold = settings.QuorumThreshold;
                if (settings.TrustedKeys is not null)
                {
                    foreach (var key in settings.TrustedKeys)
                    {
                        if (key is null || string.IsNullOrWhiteSpace(key.Fingerprint) || key.ArmoredPublicKey is null)
                            continue;
                        trusted[key.Fingerprint] = key.ArmoredPublicKey;
                    }
                }
            }

            foreach (var item in fetched)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessOneAsync(item, state, settings, trusted, quorumThreshold, recorded);
                }
                catch (Exception e)
                {
                    // One bad entry must not abort the rest of the sweep - see the class doc
                    // comment. Deliberately not recorded to the ledger here: the failure may be
                    // WHY recording itself is impossible (e.g. a corrupt entry object), and
                    // retrying a write inside its own exception handler risks repeating the same
                    // failure. The advisory is simply re-evaluated next poll.
                    logger.LogError(e, "SecSwitch failed to process advisory {Id}; skipping it this sweep",
                        item?.Id);
                }
            }
        }
        catch (Exception e)
        {
            // Structural backstop for anything above the per-item loop itself - see the class doc
            // comment. Returns whatever was already recorded rather than throwing.
            logger.LogError(e, "SecSwitch advisory sweep failed unexpectedly");
        }

        return recorded;
    }

    async Task ProcessOneAsync(
        FetchedAdvisory item, InstanceState state, SecSwitchSettings? settings,
        IReadOnlyDictionary<string, string> trusted, int quorumThreshold, List<LedgerEntry> recorded)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id))
            return; // Nothing to key a ledger entry by - never recorded is the same as ignored today.

        // Hard requirement 3 (the action-loop guard): if SecSwitch stops the server over an
        // unfixable core advisory and a supervisor restarts it, IsHandledAsync is the only thing
        // preventing it re-acting on the same advisory forever - a crash loop, not a kill switch.
        // Never bypassed, and always the first check for an advisory reached this far.
        if (await ledger.IsHandledAsync(item.Id))
            return;

        var entry = new LedgerEntry { AdvisoryId = item.Id, RecordedAt = DateTimeOffset.UtcNow };

        // Hard requirement 1: only ever persist ContentHash when the fetch that produced this item
        // gathered every signature file the feed listed. A fetch truncated by the fetcher's own
        // request budget or the overall poll deadline can hand back a PARTIAL signature set with
        // SignaturesComplete=false; persisting ContentHash in that case would mark the advisory
        // "already seen" in AdvisoryFetcher's own dedup (fed the set of recorded content hashes on
        // the next poll), so a signature set that can never grow could never reach quorum - a
        // silent, permanent kill-switch failure. Applied uniformly, before branching on outcome
        // below, so every status this method can produce - Rejected included - gets the same
        // treatment; the gate is on SignaturesComplete alone, never on what happens afterwards.
        entry.ContentHash = item.SignaturesComplete ? item.ContentHash ?? "" : "";

        if (item.PayloadBytes is null)
        {
            // AdvisoryParser.TryParse was never contracted against a null payload (JsonDocument.Parse
            // throws ArgumentNullException, not a caught JsonException) - guard explicitly rather
            // than lean on the per-item try/catch alone, so this still surfaces as a visible,
            // recorded Rejected entry for the admin instead of silently vanishing from the sweep.
            entry.Status = "Rejected";
            entry.Reason = "Advisory payload is missing.";
            await RecordAsync(entry, recorded);
            return;
        }

        if (!AdvisoryParser.TryParse(item.PayloadBytes, out var advisory, out var parseError))
        {
            entry.Status = "Rejected";
            entry.Reason = parseError ?? "Unparseable advisory.";
            await RecordAsync(entry, recorded);
            return;
        }

        entry.Title = advisory!.Title;
        entry.Identifier = advisory.Identifier;
        entry.Severity = advisory.Severity.ToString();

        // Verification precedes policy: nothing below this point is reached for an advisory that
        // failed quorum.
        var verification = AdvisoryVerifier.Verify(item.PayloadBytes, item.ArmoredSignatures, trusted, quorumThreshold);

        if (!verification.QuorumMet)
        {
            entry.Status = "Unverified";
            entry.Reason =
                $"Quorum not met ({verification.TrustedValidCount}/{verification.Required} trusted signatures).";
            logger.LogWarning("SecSwitch rejected advisory {Id}: {Reason}", item.Id, entry.Reason);
            await RecordAsync(entry, recorded);
            return;
        }

        // PolicyResolver.Resolve is declared to take a non-nullable SecSwitchSettings but, like
        // AdvisoryApplicability.IsApplicable, checks for null internally and fails closed to
        // SecSwitchAction.None rather than throwing (Task 6) - the same contract
        // PolicyResolverTests itself relies on via a null! argument. settings is threaded through
        // as received (possibly null - see the class doc comment on ProcessAsync's own defensive
        // handling above) rather than assumed non-null here.
        var decision = PolicyResolver.Resolve(advisory, state, settings!);
        entry.Action = decision.Action.ToString();
        entry.Reason = decision.Reason;

        if (decision.Action == SecSwitchAction.None)
        {
            // Hard requirement 2: PolicyResolver folds "genuinely not applicable" and "resolved an
            // identifier but the installed version is indeterminate" into the SAME
            // SecSwitchAction.None - the distinction survives only in Reason's exact wording (the
            // phrase "could not be determined", verbatim - see AdvisoryApplicability.IsApplicable
            // and PolicyResolver.Resolve). An indeterminate result means we do not actually know
            // whether the instance is affected, which is a needs-attention case, not a clean
            // "nothing to do" - it must not silently disappear under the same status a genuine
            // not-applicable advisory gets.
            entry.Status = decision.Reason.Contains("could not be determined", StringComparison.OrdinalIgnoreCase)
                ? "NeedsAttention"
                : "NotApplicable";
            await RecordAsync(entry, recorded);
            return;
        }

        var outcome = await executor.ExecuteAsync(decision.Action, advisory);
        entry.Status = decision.Action == SecSwitchAction.Notify ? "NeedsDecision" : "Acted";
        entry.Reason = $"{decision.Reason} {outcome}".Trim();
        await RecordAsync(entry, recorded);
    }

    async Task RecordAsync(LedgerEntry entry, List<LedgerEntry> recorded)
    {
        await ledger.RecordAsync(entry);
        recorded.Add(entry);
    }

    /// <summary>
    /// <see cref="IPeriodicTask"/> entry point. SecSwitchMonitor's constructor deliberately carries
    /// no advisory source, settings store, or instance-state provider - <see cref="ProcessAsync"/>
    /// takes all three as parameters instead, which is what makes it independently testable.
    /// Gathering those for a real poll (fetching advisories over HTTP, reading
    /// <see cref="SecSwitchSettings"/> from <c>ISettingsRepository</c>, and building
    /// <see cref="InstanceState"/> from the installed-plugin list, the running core version, and
    /// SSH availability) is later-task wiring, not part of this task's scope (see task-11-report.md).
    /// Until that wiring exists, this is a deliberate, logged no-op - never a silent one - so
    /// registering this type as a scheduled task before that wiring lands is visible in the logs
    /// rather than silently appearing to run while actually protecting nothing. It still satisfies
    /// the same never-throw contract as <see cref="ProcessAsync"/>.
    /// </summary>
    public Task Do(CancellationToken cancellationToken)
    {
        logger.LogDebug(
            "SecSwitchMonitor.Do invoked, but no advisory source is wired to this instance yet; nothing to do.");
        return Task.CompletedTask;
    }
}
