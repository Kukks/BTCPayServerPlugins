using System;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class PolicyResolver
{
    /// <summary>Advisories state facts; only this local policy decides the action.</summary>
    public static PolicyDecision Resolve(Advisory advisory, InstanceState state, SecSwitchSettings settings,
        bool allowFix = true)
    {
        if (!settings.Enabled)
            return new PolicyDecision(SecSwitchAction.None, "SecSwitch is disabled.");

        if (!AdvisoryApplicability.IsApplicable(advisory, state, out var installed, out var reason))
            return new PolicyDecision(SecSwitchAction.None, reason);

        var isCore = string.Equals(advisory.Identifier, AdvisoryApplicability.CoreIdentifier,
            StringComparison.OrdinalIgnoreCase);
        var hasFix = AdvisoryApplicability.HasUsableFix(advisory, installed!);
        if (!hasFix && !string.IsNullOrWhiteSpace(advisory.FixedVersion))
            reason += $" Its fixedVersion {advisory.FixedVersion} is not a newer version outside the affected range, so it is ignored.";

        var mitigation = isCore ? SecSwitchAction.Lockdown : SecSwitchAction.DisablePlugin;
        if (hasFix && allowFix)
        {
            if (isCore && state.CanUpdateCore && settings.AutoUpdateCore)
                return new PolicyDecision(SecSwitchAction.UpdateCore, reason);
            if (!isCore && settings.IsPluginAutoUpdateEnabled(advisory.Identifier))
                return new PolicyDecision(SecSwitchAction.UpdatePlugin, reason);
        }
        if (hasFix && !allowFix)
            reason += " Automatic update was deferred because the proposed fix could not be checked against all relevant advisories.";

        return settings.AutoApply
            ? new PolicyDecision(mitigation, reason)
            : new PolicyDecision(SecSwitchAction.Notify,
                $"The kill switch is off. Review the advisory for {advisory.Identifier}. {reason}", mitigation);
    }
}
