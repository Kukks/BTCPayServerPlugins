using System;
using System.Linq;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class PolicyResolver
{
    // Task 13 review, Finding I2 (Important) fix: load-bearing wording, mirroring
    // AdvisoryApplicability.IndeterminateVersionPhrase's own convention exactly - SecSwitchMonitor
    // greps this exact phrase out of Resolve's `reason` output to tell "SSH connectivity has not
    // finished verifying yet, try again next poll" apart from a genuine, permanent "no SSH, shutting
    // down" decision. See the SshVerificationPending branch below and InstanceState.SshVerificationPending's
    // own doc comment for the full reasoning.
    public const string SshVerificationPendingPhrase = "SSH connectivity has not finished verifying";

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

        // Task 13 review, Finding I2 (Important) fix: a fixable core advisory reaching this exact
        // ambiguous state - SSH IS configured, but CheckConfigurationHostedService has not (yet, or
        // ever) reported CanUseSSH true - must never be resolved as ShutdownCore. Unlike the ternary
        // below (unchanged for every other case, including "SSH genuinely not configured at all",
        // where SshVerificationPending is false and this branch is never reached), this returns
        // BEFORE `intended` is computed and skips manual-mode/notify-pin/severity-gate entirely,
        // exactly like the null-state and not-applicable early returns above - there is no safe
        // "intended action" to report here, only "wait and re-evaluate". See
        // InstanceState.SshVerificationPending's own doc comment for why getting this wrong is
        // effectively permanent: SecSwitchMonitor.RecordAsync caches a terminal Acted status, which no
        // later poll could ever correct back to the real UpdateCore.
        if (isCore && hasFix && !state.CanUseSsh && state.SshVerificationPending)
        {
            return new PolicyDecision(SecSwitchAction.None,
                $"{SshVerificationPendingPhrase} for {advisory.Identifier}; deferring the update-vs-shutdown decision to a later poll.");
        }

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
