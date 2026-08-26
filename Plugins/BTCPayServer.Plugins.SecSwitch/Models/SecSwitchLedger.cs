using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchLedger
{
    public DateTimeOffset? LastStartedAt { get; set; }
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
    /// is a known, explicitly out-of-scope gap for a later task - see task-11-report.md.</summary>
    public const string NotApplicable = "NotApplicable";

    /// <summary>Quorum met; PolicyResolver resolved SecSwitchAction.None specifically because the
    /// installed version could not be determined, or InstanceState itself was null - we do not
    /// actually know whether this instance is affected. Final only because we could not look
    /// properly - never cached; must be re-fetched.</summary>
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
