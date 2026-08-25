using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class AdvisoryApplicability
{
    public const string CoreIdentifier = "BTCPayServer";

    public static bool IsApplicable(Advisory advisory, InstanceState state,
        out Version? installedVersion, out string reason)
    {
        installedVersion = null;

        if (string.Equals(advisory.Identifier, CoreIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            installedVersion = state.CoreVersion;
        }
        else if (state.InstalledPlugins.TryGetValue(advisory.Identifier, out var v))
        {
            installedVersion = v;
        }
        else
        {
            reason = $"{advisory.Identifier} is not installed on this instance.";
            return false;
        }

        // A blank condition parses to VersionCondition.Yes, which matches every version.
        // Treat it as malformed rather than as a wildcard match.
        if (string.IsNullOrWhiteSpace(advisory.AffectedVersions))
        {
            reason = "Advisory has an empty affectedVersions range.";
            installedVersion = null;
            return false;
        }

        if (!VersionCondition.TryParse(advisory.AffectedVersions, out var condition))
        {
            reason = $"affectedVersions '{advisory.AffectedVersions}' could not be parsed.";
            installedVersion = null;
            return false;
        }

        if (!condition.IsFulfilled(installedVersion))
        {
            reason = $"Installed version {installedVersion} is not affected by {advisory.AffectedVersions}.";
            installedVersion = null;
            return false;
        }

        reason = $"Installed version {installedVersion} matches {advisory.AffectedVersions}.";
        return true;
    }
}
