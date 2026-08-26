using System;
using System.Linq;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class PolicyResolver
{
    public static PolicyDecision Resolve(Advisory advisory, InstanceState state, SecSwitchSettings settings)
    {
        if (!settings.Enabled)
            return new PolicyDecision(SecSwitchAction.None, "SecSwitch is disabled.");

        if (advisory.Revoked)
            return new PolicyDecision(SecSwitchAction.None, $"Advisory {advisory.Id} is revoked.");

        // Not-applicable is kept as a single undifferentiated None mapping, including the
        // indeterminate case (installed version could not be determined) - a later task keys off
        // the exact phrase "could not be determined" inside `reason`, so that phrase must survive
        // verbatim into the decision's Reason rather than being replaced or paraphrased here.
        if (!AdvisoryApplicability.IsApplicable(advisory, state, out _, out var reason))
            return new PolicyDecision(SecSwitchAction.None, reason);

        var isCore = string.Equals(advisory.Identifier, AdvisoryApplicability.CoreIdentifier,
            StringComparison.OrdinalIgnoreCase);
        var hasFix = !string.IsNullOrWhiteSpace(advisory.FixedVersion);

        // Advisories state facts (target, affected range, fixed version, severity); local policy
        // alone decides the action. Nothing above this line inspects settings, and nothing below
        // this line inspects advisory content beyond the facts already extracted into isCore/hasFix.
        var intended = isCore
            ? hasFix && state.CanUseSsh ? SecSwitchAction.UpdateCore : SecSwitchAction.ShutdownCore
            : hasFix && settings.PreferUpdateOverDisable ? SecSwitchAction.UpdatePlugin : SecSwitchAction.DisablePlugin;

        if (!settings.AutoApply)
            return new PolicyDecision(SecSwitchAction.Notify,
                $"Manual mode: {intended} required for {advisory.Identifier}. {reason}");

        if (settings.NotifyOnlyIdentifiers.Contains(advisory.Identifier, StringComparer.OrdinalIgnoreCase))
            return new PolicyDecision(SecSwitchAction.Notify,
                $"{advisory.Identifier} is pinned to notify-only. {intended} required.");

        if (settings.SeverityGateEnabled && advisory.Severity < AdvisorySeverity.High)
            return new PolicyDecision(SecSwitchAction.Notify,
                $"Severity {advisory.Severity} is below the automatic-action threshold. {intended} required.");

        return new PolicyDecision(intended, reason);
    }
}
