namespace BTCPayServer.Plugins.SecSwitch.Models;

public enum AdvisorySeverity { Low, Medium, High, Critical }

public sealed record AdvisoryDependency(string Identifier, string AffectedVersions);

public sealed class Advisory
{
    public string Id { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string AffectedVersions { get; set; } = "";
    public string? FixedVersion { get; set; }
    public AdvisoryDependency? Requires { get; set; }
    public AdvisorySeverity Severity { get; set; }
    public string Title { get; set; } = "";
}
