using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchLedger
{
    public Dictionary<string, LedgerEntry> Entries { get; set; } = [];
    public DateTimeOffset? LastPollAt { get; set; }

    /// <summary>Null when the last poll succeeded.</summary>
    public string? LastPollError { get; set; }
}

public static class LedgerStatus
{
    public const string Acted = "Acted";
    public const string NeedsDecision = "NeedsDecision";
    public const string NeedsAttention = "NeedsAttention";
    public const string Resolved = "Resolved";
}

public sealed class LedgerEntry
{
    public string AdvisoryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Status { get; set; } = "";
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public bool Suppressed { get; set; }

    /// <summary>True while this entry keeps the server in lockdown; cleared once the advisory stops applying.</summary>
    public bool LockedDown { get; set; }
}
