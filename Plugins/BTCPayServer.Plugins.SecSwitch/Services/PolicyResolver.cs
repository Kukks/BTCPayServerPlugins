using System;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class PolicyResolver
{
    /// <summary>Advisories state facts; only this local policy decides the action.</summary>
    public static PolicyDecision Resolve(Advisory advisory, InstanceState state, SecSwitchSettings settings)
    {
        if (!settings.Enabled)
            return new PolicyDecision(SecSwitchAction.None, "SecSwitch is disabled.");

        if (!AdvisoryApplicability.IsApplicable(advisory, state, out _, out var reason))
            return new PolicyDecision(SecSwitchAction.None, reason);

        var isCore = string.Equals(advisory.Identifier, AdvisoryApplicability.CoreIdentifier,
            StringComparison.OrdinalIgnoreCase);
        var hasFix = !string.IsNullOrWhiteSpace(advisory.FixedVersion);

        var intended = isCore
            ? hasFix && state.CanUpdateCore ? SecSwitchAction.UpdateCore : SecSwitchAction.Lockdown
            : hasFix && settings.PreferUpdateOverDisable ? SecSwitchAction.UpdatePlugin : SecSwitchAction.DisablePlugin;

        return settings.AutoApply
            ? new PolicyDecision(intended, reason)
            : new PolicyDecision(SecSwitchAction.Notify,
                $"Manual mode: {intended} required for {advisory.Identifier}. {reason}", intended);
    }
}
