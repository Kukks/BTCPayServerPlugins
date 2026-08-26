using System;
using System.Linq;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class PolicyResolver
{
    public static PolicyDecision Resolve(Advisory advisory, InstanceState state, SecSwitchSettings settings)
    {
        // Fail closed on null input, mirroring AdvisoryApplicability.IsApplicable (see its own
        // comment): a later task loops Resolve over parsed advisories, and an uncaught exception
        // here would silently abort the whole sweep rather than just skipping the one bad
        // advisory. Checked in the same order these arguments are first dereferenced below.
        if (settings is null)
            return new PolicyDecision(SecSwitchAction.None, "Settings is null.");

        if (advisory is null)
            return new PolicyDecision(SecSwitchAction.None, "Advisory is null.");

        if (state is null)
            return new PolicyDecision(SecSwitchAction.None, "Instance state is null.");

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
                $"Manual mode: {intended} required for {advisory.Identifier}. {reason}", intended);

        // A null NotifyOnlyIdentifiers is treated as "no pins", not dereferenced - settings is
        // admin/config-supplied input the same way advisory and state are, so it gets the same
        // fail-closed treatment rather than throwing out of the loop a later task drives this from.
        if (settings.NotifyOnlyIdentifiers is not null &&
            settings.NotifyOnlyIdentifiers.Contains(advisory.Identifier, StringComparer.OrdinalIgnoreCase))
            return new PolicyDecision(SecSwitchAction.Notify,
                $"{advisory.Identifier} is pinned to notify-only. {intended} required.", intended);

        if (settings.SeverityGateEnabled && advisory.Severity < AdvisorySeverity.High)
            return new PolicyDecision(SecSwitchAction.Notify,
                $"Severity {advisory.Severity} is below the automatic-action threshold. {intended} required.", intended);

        return new PolicyDecision(intended, reason);
    }
}
