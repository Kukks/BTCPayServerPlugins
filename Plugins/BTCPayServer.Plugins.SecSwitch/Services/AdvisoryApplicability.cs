using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class AdvisoryApplicability
{
    public const string CoreIdentifier = "BTCPayServer";

    // Load-bearing wording, not incidental phrasing: a later component (SecSwitchMonitor) greps
    // this exact phrase out of IsApplicable's `reason` output - via PolicyResolver.Resolve, which
    // passes the reason through verbatim - to tell "we resolved an identifier but could not
    // establish its installed version" apart from a clean, genuine not-applicable result. Extracted
    // to a shared constant (Task 11 review, Finding M5) so that coupling is compile-enforced rather
    // than resting on two files independently typing the same literal string; changing this value
    // changes what SecSwitchMonitor treats as a needs-attention result.
    public const string IndeterminateVersionPhrase = "could not be determined";

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

        // System.Version's comparison operators treat a null operand as "less than everything"
        // rather than throwing, so IsFulfilled(null) silently returns a result that depends on
        // which operator the advisory happened to use (">=1.0.0" => false, "<2.5.0" => true) -
        // a false negative or a nonsensical "applicable but the version is unknown" outcome,
        // decided by accident rather than by design. Neither is acceptable for a fail-closed
        // component, so treat "we resolved an identifier but its version is null" as its own
        // explicit, deterministic outcome rather than letting it fall into IsFulfilled.
        if (installedVersion is null)
        {
            reason = $"Installed version of {advisory.Identifier} {IndeterminateVersionPhrase}.";
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
