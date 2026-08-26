using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchLedger
{
    public DateTimeOffset? LastStartedAt { get; set; }

    // Task 13 review, Finding I1 (Important) fix: this was originally a single `bool
    // TrustRootBootstrapped`, latched true the first time the embedded trust-root resource was
    // successfully PARSED, regardless of how many keys it actually contained. Today's shipped
    // resource is "keys": [] - every existing instance would latch true having installed nothing, and
    // a LATER plugin release that finally populates the bundle would then never install those keys on
    // any already-upgraded instance, because the latch was already permanently set. That defeats the
    // only reason this bootstrap exists.
    //
    // Tracking the set of fingerprints the bundle has ever OFFERED - rather than a single "did this
    // ever run" bool - fixes that: a fingerprint not yet in this list is genuinely new and gets
    // considered (added if not already trusted); a fingerprint already in this list is never
    // reconsidered, no matter how many more times the bundle lists it, which is exactly what "an
    // admin who deliberately removed a bundled key must not have it silently reappear" requires. A
    // plain List<string>, not a HashSet: unlike Dictionary's comparer (see Entries's own history
    // below), a List has no comparer to lose on a JSON round trip in the first place - callers always
    // wrap this in a fresh OrdinalIgnoreCase HashSet at the point they check membership (matching the
    // fingerprint-comparison convention used everywhere else in this plugin) rather than relying on
    // List.Contains's default (ordinal, case-sensitive) comparison.
    //
    // Placed on the ledger rather than SecSwitchSettings deliberately (unchanged reasoning from the
    // original design): SecSwitchController's Settings POST handler already restores
    // TrustedKeys/NotifyOnlyIdentifiers from the persisted record before saving specifically because
    // that form does not round-trip them (see its own doc comment) - an equivalent field added to
    // SecSwitchSettings instead would need that same restore-before-save treatment remembered on
    // every future settings-mutating endpoint, and forgetting it even once would silently reset this
    // and re-offer everything. The ledger is never partially overwritten by a web form; only
    // LedgerStore's own methods ever mutate it.
    public List<string> OfferedTrustRootFingerprints { get; set; } = [];

    public Dictionary<string, LedgerEntry> Entries { get; set; } = [];
}

/// <summary>
/// The full vocabulary of values <see cref="LedgerEntry.Status"/> can hold. Extracted to shared
/// constants (Task 11 review, Finding R3): these strings became load-bearing, not cosmetic, the
/// moment <see cref="Services.LedgerStore.IsTerminalStatus"/> started keying off them to decide
/// BOTH whether an advisory is re-evaluated on a future poll (<see cref="Services.LedgerStore.IsActedAsync"/>)
/// AND whether its ContentHash may be cached against re-download (see
/// <c>Services.SecSwitchMonitor.RecordAsync</c>'s own doc comment) - a typo in a raw string literal
/// on either the producer (SecSwitchMonitor, which sets <see cref="LedgerEntry.Status"/>) or
/// consumer (LedgerStore, which reads it back) side would silently disarm one or the other with no
/// compiler error.
/// </summary>
public static class LedgerStatus
{
    /// <summary>The advisory's payload could not be parsed at all (missing, or malformed JSON).
    /// Final only because verification could not even be attempted - never cached; must be
    /// re-fetched.</summary>
    public const string Rejected = "Rejected";

    /// <summary>Parsed successfully, but the armored signatures presented did not meet GPG quorum.
    /// Final only because we could not look properly - never cached; must be re-fetched, since a
    /// later poll (more co-signers, or an honest mirror) could change the outcome.</summary>
    public const string Unverified = "Unverified";

    /// <summary>Quorum met; PolicyResolver resolved SecSwitchAction.None for a reason that does NOT
    /// indicate an indeterminate installed version or a null InstanceState - genuinely not
    /// applicable to this instance (including a revoked advisory, or one targeting a plugin/core
    /// this instance does not run). Final "on content": nothing about re-fetching byte-identical
    /// advisory.json could ever change this verdict, so it is safe (and necessary - see
    /// MaxAdvisoriesPerPoll's own starvation-avoidance reasoning) to cache. Re-evaluating this
    /// advisory later because LOCAL state changed (e.g. the operator installs the affected plugin)
    /// is a known, explicitly out-of-scope gap for a later task - see task-11-report.md.
    ///
    /// A second, DIFFERENT gap worth recording alongside that one (Task 11 review, Doc 2):
    /// AdvisoryFetcher's own dedup is keyed by ContentHash, while
    /// <see cref="Services.LedgerStore.IsActedAsync"/> - which this status feeds into being
    /// terminal for - is keyed by advisory id. If an advisory were ever amended in place under a
    /// STABLE id (same id, new ContentHash), it would be fetched again (the new hash is unknown to
    /// the fetcher's dedup) but then turned away by the id-keyed latch on sight of a prior
    /// NotApplicable entry, even though the amended content might resolve differently. This is
    /// safe ONLY because the feed specification forbids amend-in-place: advisory.json must never
    /// be edited after publication, since doing so would invalidate every signature over it - a
    /// genuine correction is published as a NEW advisory (via <c>supersedes</c>) or a
    /// <c>revoked</c> replacement, each under its own id and its own quorum, so an advisory
    /// "amended" under a stable id would fail quorum on its own, independent of this latch. If
    /// that feed-spec invariant were ever relaxed, this identity mismatch becomes live.</summary>
    public const string NotApplicable = "NotApplicable";

    /// <summary>Quorum met; PolicyResolver resolved SecSwitchAction.None (or, for a core advisory,
    /// deferred entirely - see PolicyResolver.SshVerificationPendingPhrase) for one of several reasons
    /// SecSwitch could not resolve on its own: the installed version could not be determined,
    /// InstanceState itself was null, or - Task 13 review round 2, Finding R1 - a fixable core
    /// advisory was reached while SSH is configured but CheckConfigurationHostedService has not (yet,
    /// or ever) reported success. In every case we do not actually know whether/how to act. Final only
    /// because we could not look properly - never cached; must be re-fetched. The SSH case specifically
    /// can persist INDEFINITELY (the connectivity probe retries forever but never guarantees success),
    /// which is why SecSwitchPeriodicTask.IsNotifiable now surfaces this status via the admin bell
    /// notification and the alert banner from the first poll it is recorded on, not only in the audit
    /// log - see that method's own doc comment.</summary>
    public const string NeedsAttention = "NeedsAttention";

    /// <summary>Quorum met and an action was computed, but policy (manual mode, a notify-only pin,
    /// or the severity gate) is holding it open for an admin decision rather than applying it
    /// automatically. Final "on content" in the sense that matters here: an admin resolves this
    /// through the ledger itself, not by the advisory changing - safe to cache.</summary>
    public const string NeedsDecision = "NeedsDecision";

    /// <summary>Quorum met and SecSwitch applied (or attempted) the resulting action automatically.
    /// Final "on content" - safe to cache.</summary>
    public const string Acted = "Acted";

    /// <summary>An admin explicitly suppressed this advisory via LedgerStore.SuppressAsync - the
    /// escape hatch, always absolute. Safe to cache.</summary>
    public const string Suppressed = "Suppressed";

    /// <summary>An admin explicitly reversed a suppression via LedgerStore.UnsuppressAsync (Task 12
    /// review, Finding I3). A transient marker, not a terminal outcome - it exists only to be a
    /// NON-terminal Status so Services.LedgerStore.IsTerminalStatus (and therefore IsActedAsync)
    /// stop latching on this entry, making it eligible for a fresh SecSwitchMonitor evaluation on
    /// the next poll. Never cached (see IsTerminalStatus) - the next RecordAsync call for this
    /// advisory id overwrites both this Status and the ContentHash UnsuppressAsync also cleared
    /// with whatever that fresh evaluation produces.</summary>
    public const string Unsuppressed = "Unsuppressed";
}

public sealed class LedgerEntry
{
    public string AdvisoryId { get; set; } = "";

    // Deviation from the Task 6 brief (ruling applied 2026-08-25): the brief's LedgerEntry has no
    // content-hash field. A later task fetches advisories from a feed and must skip ones it has
    // already seen by comparing content hashes - the brief's design otherwise has nothing but
    // advisory ids to compare against feed-supplied content hashes, which would never match and
    // would re-download every advisory forever. Persisting the hash here is the fix. Nothing in
    // Task 6 populates it; a later task does.
    public string ContentHash { get; set; } = "";
    public string Title { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Status { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public bool Suppressed { get; set; }
}
