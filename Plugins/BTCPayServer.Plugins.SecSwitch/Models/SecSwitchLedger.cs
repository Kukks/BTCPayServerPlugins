using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchLedger
{
    public DateTimeOffset? LastStartedAt { get; set; }
    public Dictionary<string, LedgerEntry> Entries { get; set; } = [];
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
