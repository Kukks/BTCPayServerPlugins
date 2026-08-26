using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// Deliberately does NOT implement <c>IPeriodicTask</c> (Task 11 review, Finding M6): this class's
/// constructor carries no advisory source, settings store, or instance-state provider - gathering
/// those for a real poll is a later task's job (see task-11-report.md). Implementing the scheduled-
/// task interface here anyway would advertise schedulability that isn't backed by anything, which
/// is exactly the "looks healthy, protects nothing" failure mode this task exists to avoid: a
/// future `AddScheduledTask&lt;SecSwitchMonitor&gt;()` would compile and run forever, doing nothing.
/// The later periodic-task class owns that interface and calls <see cref="ProcessAsync"/> once it
/// can actually supply real inputs.
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
/// before any single advisory is reached.
///
/// Verification always precedes policy: <see cref="PolicyResolver.Resolve"/> and
/// <see cref="ActionExecutor.ExecuteAsync"/> are only ever reached for an advisory whose GPG quorum
/// already checked out - nothing here resolves or executes anything for an advisory that failed
/// quorum.
/// </summary>
public sealed class SecSwitchMonitor(
    ActionExecutor executor,
    LedgerStore ledger,
    ILogger<SecSwitchMonitor> logger)
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

        // Finding I3: SecSwitchSettings.Enabled has no initializer, so it defaults to false, and
        // PolicyResolver's "SecSwitch is disabled" None-reason carries no distinguishing phrase -
        // without this check every advisory would be recorded as "NotApplicable", a status that
        // affirmatively tells the admin "you are not affected", which is false: the advisory was
        // simply never evaluated because the plugin was off (or settings itself was unavailable).
        // Checked before a single advisory is even looked at, so nothing is written to the ledger
        // at all while disabled - a backlog that accumulated before an admin turns SecSwitch on is
        // therefore never latched by anything (there is nothing recorded yet to latch), and it gets
        // a normal first look the moment Enabled flips true.
        if (settings?.Enabled != true)
            return recorded;

        try
        {
            // Built once, defensively, outside the per-advisory loop: settings.TrustedKeys is
            // admin-supplied configuration, but a naive ToDictionary over it throws
            // ArgumentException the moment two entries share a fingerprint (case-insensitively) -
            // which would abort the ENTIRE sweep, not just one advisory. Mirrors
            // TrustStore.TryApplyRotation's own defensive dictionary-building loop: null/blank
            // entries are skipped, and a duplicate fingerprint is last-write-wins, silently, rather
            // than thrown. settings itself is guaranteed non-null here - the only way past the
            // Enabled check above is settings being non-null AND Enabled == true.
            var trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settings.TrustedKeys is not null)
            {
                foreach (var key in settings.TrustedKeys)
                {
                    if (key is null || string.IsNullOrWhiteSpace(key.Fingerprint) || key.ArmoredPublicKey is null)
                        continue;
                    trusted[key.Fingerprint] = key.ArmoredPublicKey;
                }
            }
            var quorumThreshold = settings.QuorumThreshold;

            foreach (var item in fetched)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessOneAsync(item, state, settings, trusted, quorumThreshold, recorded, ct);
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
        FetchedAdvisory item, InstanceState state, SecSwitchSettings settings,
        IReadOnlyDictionary<string, string> trusted, int quorumThreshold, List<LedgerEntry> recorded,
        CancellationToken ct)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Id))
            return; // Nothing to key a ledger entry by - never recorded is the same as ignored today.

        // Hard requirement 3 (the action-loop guard) / Finding C1 fix: gates on IsActedAsync, NOT
        // IsHandledAsync. IsHandledAsync latches on ANY recorded entry regardless of status - which
        // would silently defeat hard requirement 1's own ContentHash-withholding fix, since the
        // re-fetch it buys on the next poll would arrive here and be turned away anyway because
        // SOME (non-terminal) entry already exists under this id. IsActedAsync only latches on a
        // TERMINAL outcome (see LedgerStore.IsTerminalStatus's own doc comment for the current
        // set), so an advisory recorded as Rejected/Unverified/NeedsAttention remains eligible for
        // re-evaluation on every later poll until it either resolves to a terminal status or an
        // admin suppresses it. Never bypassed, and always the first check for an advisory reached
        // this far.
        if (await ledger.IsActedAsync(item.Id))
            return;

        var entry = new LedgerEntry { AdvisoryId = item.Id, RecordedAt = DateTimeOffset.UtcNow };

        // The candidate ContentHash is carried through here UNCONDITIONALLY - RecordAsync (see its
        // own doc comment - Task 11 review, Finding R1) decides whether to keep it or withhold it
        // once entry.Status is final, since that decision depends on the FINAL outcome, which is
        // not known yet at this point in the method.
        entry.ContentHash = item.ContentHash ?? "";

        if (item.PayloadBytes is null)
        {
            // Defence in depth, not a workaround for a real gap: by inspection of the call site and
            // .NET's byte[] -> ReadOnlyMemory<byte> conversion semantics, AdvisoryParser.TryParse
            // does NOT throw on a null payload - JsonDocument.Parse(byte[]) takes the byte[] via an
            // implicit conversion to ReadOnlyMemory<byte>, which maps a null array to an empty
            // ReadOnlyMemory rather than throwing, so parsing fails with JsonReaderException (a
            // JsonException subtype) exactly like any other malformed input, and TryParse's own
            // catch already handles it (Task 11 review, Finding I4 - retracts an earlier, incorrect
            // claim in this comment that TryParse was unguarded here). Kept anyway because it
            // produces a clearer, more specific Reason for the admin than a generic JSON parse
            // error would.
            entry.Status = LedgerStatus.Rejected;
            entry.Reason = "Advisory payload is missing.";
            await RecordAsync(entry, item.SignaturesComplete, recorded);
            return;
        }

        if (!AdvisoryParser.TryParse(item.PayloadBytes, out var advisory, out var parseError))
        {
            entry.Status = LedgerStatus.Rejected;
            // parseError can embed a JsonException.Message derived from attacker-supplied bytes
            // (Task 11 review, Finding I2) - sanitized before it reaches the ledger, matching how
            // ActionExecutor.Sanitize is already used for every other attacker-influenced string
            // that reaches an admin-facing surface.
            entry.Reason = ActionExecutor.Sanitize(parseError ?? "Unparseable advisory.");
            await RecordAsync(entry, item.SignaturesComplete, recorded);
            return;
        }

        // Finding I2: Title/Identifier/Severity are advisory-derived, attacker-influenced free text
        // (AdvisoryParser caps none of them in length), assigned and persisted here BEFORE
        // verification even runs - so an unverified, forged advisory.json up to the parser's byte
        // cap could otherwise write an oversized, control-character-laden blob into the single
        // SecSwitchLedger settings row and, eventually, an admin audit page. Sanitized via the same
        // ActionExecutor.Sanitize routine SecSwitchNotifications already uses for identical reasons
        // (see its own doc comment: "before either reaches the admin UI, the ledger, or a log
        // sink"). This only affects the LEDGER'S copy - the real `advisory` object below is left
        // untouched, since AdvisoryApplicability/ActionExecutor need its real Identifier to match
        // and act on the correct installed plugin.
        entry.Title = ActionExecutor.Sanitize(advisory!.Title);
        entry.Identifier = ActionExecutor.Sanitize(advisory.Identifier);
        entry.Severity = ActionExecutor.Sanitize(advisory.Severity.ToString());

        // Verification precedes policy: nothing below this point is reached for an advisory that
        // failed quorum.
        var verification = AdvisoryVerifier.Verify(item.PayloadBytes, item.ArmoredSignatures, trusted, quorumThreshold);

        if (!verification.QuorumMet)
        {
            entry.Status = LedgerStatus.Unverified;
            entry.Reason =
                $"Quorum not met ({verification.TrustedValidCount}/{verification.Required} trusted signatures).";
            logger.LogWarning("SecSwitch rejected advisory {Id}: {Reason}", item.Id, entry.Reason);
            await RecordAsync(entry, item.SignaturesComplete, recorded);
            return;
        }

        var decision = PolicyResolver.Resolve(advisory, state, settings);
        entry.Action = decision.Action.ToString();

        // Hard requirement 2 / Finding I3 (the "use your judgement" note): PolicyResolver folds
        // several distinct SecSwitchAction.None reasons into ONE action value - genuinely not
        // applicable, AND "resolved an identifier but the installed version is indeterminate"
        // (AdvisoryApplicability.IndeterminateVersionPhrase, shared via the constant - Finding M5).
        // A null `state` produces a THIRD such reason ("Instance state is null.") that PolicyResolver
        // also maps to None - checked directly here via `state is null` rather than by matching yet
        // another prose string, since the real condition is already in scope and a direct type check
        // can never drift out of sync with PolicyResolver's wording the way a second string literal
        // could. Both cases mean "we do not actually know whether this instance is affected", which
        // is a needs-attention case, not a clean "nothing to do" - neither may silently disappear
        // under the same status a genuine not-applicable advisory gets. (PolicyResolver's other two
        // None reasons - null settings, null advisory - are unreachable through this method: `settings`
        // is proven non-null by the Enabled check in ProcessAsync, and `advisory` is only ever passed
        // here after a successful parse.)
        //
        // Task 13 review, Finding I2 (Important) fix: a FOURTH such reason -
        // PolicyResolver.SshVerificationPendingPhrase, for a fixable core advisory reached while SSH
        // connectivity has not finished verifying yet - joins the same NeedsAttention bucket for the
        // identical reason: "we cannot yet tell whether this should be UpdateCore or ShutdownCore" is
        // not a clean "nothing to do" either, and above all must never be cached as the terminal
        // NotApplicable (or, worse, reach the Acted branch below as ShutdownCore) - see
        // InstanceState.SshVerificationPending's own doc comment for why that would be effectively
        // permanent.
        var isIndeterminate = state is null ||
            decision.Reason.Contains(AdvisoryApplicability.IndeterminateVersionPhrase, StringComparison.OrdinalIgnoreCase) ||
            decision.Reason.Contains(PolicyResolver.SshVerificationPendingPhrase, StringComparison.OrdinalIgnoreCase);
        var sanitizedReason = ActionExecutor.Sanitize(decision.Reason); // Finding I2: decision.Reason
            // can itself embed attacker-controlled text (e.g. advisory.Identifier or
            // AffectedVersions, via AdvisoryApplicability's own reason strings) - computed once here,
            // against the ORIGINAL (unsanitized) decision.Reason for the indeterminate check above,
            // so truncation/stripping can never affect phrase detection, then reused for every Reason
            // assignment below.
        entry.Reason = sanitizedReason;

        if (decision.Action == SecSwitchAction.None)
        {
            entry.Status = isIndeterminate ? LedgerStatus.NeedsAttention : LedgerStatus.NotApplicable;
            await RecordAsync(entry, item.SignaturesComplete, recorded);
            return;
        }

        // Finding M7: LedgerStore and ActionExecutor's own public methods accept no
        // CancellationToken (confirmed by inspection of both classes), so cancellation cannot be
        // threaded INTO an in-flight call to either. This is the last point where honouring a late
        // cancellation is still possible: it stops SecSwitch from STARTING the one potentially slow
        // or destructive step in this method (an SSH call, a plugin download, or stopping the
        // process) after cancellation was requested, even though an already-started one could not
        // itself be interrupted.
        if (ct.IsCancellationRequested)
            return;

        var outcome = await executor.ExecuteAsync(decision.Action, advisory);
        entry.Status = decision.Action == SecSwitchAction.Notify ? LedgerStatus.NeedsDecision : LedgerStatus.Acted;
        // outcome is already sanitized by ActionExecutor itself before being returned; sanitizedReason
        // (computed above) covers the other half of this string.
        entry.Reason = $"{sanitizedReason} {outcome}".Trim();
        await RecordAsync(entry, item.SignaturesComplete, recorded);
    }

    /// <summary>
    /// Persists <paramref name="entry"/> and appends it to <paramref name="recorded"/>. Also owns
    /// the hash-caching decision (Task 11 review, Finding R1 - moved here from a single
    /// pre-branch assignment in <see cref="ProcessOneAsync"/>, because the decision genuinely
    /// depends on the FINAL <see cref="LedgerEntry.Status"/>, which is not known until every branch
    /// above has run):
    ///
    /// <see cref="LedgerEntry.ContentHash"/> is kept only when BOTH (a) <paramref name="signaturesComplete"/>
    /// is true - a fetch truncated by the fetcher's own request budget or the overall poll deadline
    /// must always be retried regardless of what status it produced, since a fuller signature set
    /// next poll could still change the outcome - AND (b) <see cref="LedgerStore.IsTerminalStatus"/>
    /// is true for <paramref name="entry"/>'s own <see cref="LedgerEntry.Status"/>.
    ///
    /// (Task 11 review, Doc 1) (b) shares the exact SAME <see cref="LedgerStore.IsTerminalStatus"/>
    /// predicate <see cref="LedgerStore.IsActedAsync"/> uses to gate re-action - that piece can never
    /// drift. The two call sites' FULL conditions are not identical, though: IsActedAsync is
    /// <c>entry.Suppressed || IsTerminalStatus(...)</c>, while this method's is
    /// <c>signaturesComplete &amp;&amp; IsTerminalStatus(...)</c> - the Finding R2
    /// <see cref="LedgerEntry.Suppressed"/> flag is honoured only on the re-action-latching side,
    /// never here. Consequence, currently unreachable (today's only writer of
    /// <see cref="LedgerEntry.Suppressed"/>, <see cref="LedgerStore.SuppressAsync"/>, always sets a
    /// matching terminal Status too) but worth recording rather than rediscovering later: a
    /// hypothetical entry with Suppressed true and a non-terminal Status would have its ContentHash
    /// withheld here forever - IsActedAsync would still correctly block re-ACTION on it, but
    /// AdvisoryFetcher would re-download it on every single future poll, burning a
    /// MaxAdvisoriesPerPoll success slot each time for no benefit. Fail-safe in direction (nothing
    /// is ever mis-acted on), but it is Finding R1's own starvation argument in miniature.
    ///
    /// This split matters because <see cref="AdvisoryFetcher"/>'s own dedup is the OUTER gate and
    /// dominates: it skips an index entry outright the moment its ContentHash is already known,
    /// BEFORE <c>advisory.json</c> or any signature file is ever requested again - and that hash is
    /// carried verbatim from the feed's own index, never recomputed from the payload, while
    /// signatures live in sibling files. So caching the hash for a status that is final only
    /// because SecSwitch could not evaluate the advisory properly (Rejected, Unverified,
    /// NeedsAttention) would let a single transient failure hide the advisory from every future
    /// poll's fetch, forever - not merely from this class's own re-action check. Concretely: a
    /// mirror hostile for exactly one poll can serve the genuine advisory.json alongside a
    /// signatures/index.json listing only one of two real co-signers - every LISTED file still
    /// fetches successfully, so SignaturesComplete is true, quorum fails, and without this fix the
    /// resulting Unverified entry's hash would be cached, permanently disarming the advisory even
    /// after the mirror goes back to listing both signatures. Caching for the TERMINAL statuses -
    /// including NotApplicable - is what keeps AdvisoryFetcher's own per-poll success budget
    /// (MaxAdvisoriesPerPoll, which counts successes) from being permanently exhausted by the
    /// ordinary, non-adversarial bulk of advisories that simply do not target anything this
    /// instance runs, reproducing Task 8's Critical starvation bug via successes instead of
    /// failures.
    /// </summary>
    async Task RecordAsync(LedgerEntry entry, bool signaturesComplete, List<LedgerEntry> recorded)
    {
        if (!signaturesComplete || !LedgerStore.IsTerminalStatus(entry.Status))
            entry.ContentHash = "";

        await ledger.RecordAsync(entry);
        recorded.Add(entry);
    }
}
