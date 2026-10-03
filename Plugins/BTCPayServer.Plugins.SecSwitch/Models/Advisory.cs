using System;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public enum AdvisorySeverity { Low, Medium, High, Critical }

public sealed class Advisory
{
    public string Id { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string AffectedVersions { get; set; } = "";
    public string? FixedVersion { get; set; }
    public AdvisorySeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] References { get; set; } = [];
    public DateTimeOffset PublishedAt { get; set; }
    public string? Supersedes { get; set; }
    public bool Revoked { get; set; }
}

public sealed class AdvisoryIndexEntry
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string ContentHash { get; set; } = "";
}
