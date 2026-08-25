using System;
using System.Linq;
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

        // Defensive guards: a null/malformed argument must fail closed, never throw. A later
        // task iterates advisories in a loop, and an uncaught exception there would silently
        // abort the whole sweep - for a security kill-switch that means it quietly stops
        // protecting rather than skipping the one bad advisory.
        if (advisory is null)
        {
            reason = "Advisory is null.";
            return false;
        }

        if (state is null)
        {
            reason = "Instance state is null.";
            return false;
        }

        if (state.InstalledPlugins is null)
        {
            reason = "Instance state has no installed-plugins map.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(advisory.Identifier))
        {
            reason = "Advisory has no identifier.";
            return false;
        }

        if (string.Equals(advisory.Identifier, CoreIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            installedVersion = state.CoreVersion;
        }
        else
        {
            // Case-insensitive by construction, independent of whatever comparer the caller's
            // dictionary was built with - InstalledPlugins carries no documented comparer
            // requirement, and a case mismatch here is a false negative (a vulnerable plugin
            // reported not-applicable), the dangerous direction for this component.
            var match = state.InstalledPlugins.FirstOrDefault(kv =>
                string.Equals(kv.Key, advisory.Identifier, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                reason = $"{advisory.Identifier} is not installed on this instance.";
                return false;
            }
            installedVersion = match.Value;
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
