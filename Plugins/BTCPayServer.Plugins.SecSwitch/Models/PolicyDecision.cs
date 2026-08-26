using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

/// <param name="InstalledPlugins">
/// Installed plugin identifiers mapped to their installed version. Identifier lookups against
/// this map are treated as case-insensitive by AdvisoryApplicability.IsApplicable regardless of
/// which string comparer this dictionary itself was constructed with - callers do not need to
/// use StringComparer.OrdinalIgnoreCase for correctness, though core's own plugin service does.
/// </param>
/// <param name="CanUseSsh">
/// True only once an actual SSH connectivity check has succeeded - see
/// <c>CheckConfigurationHostedService.CanUseSSH</c>, this plugin's only real source for this value.
/// </param>
/// <param name="SshVerificationPending">
/// Task 13 review, Finding I2 (Important) fix: true only when SSH IS configured
/// (<c>BTCPayServerOptions.SSHSettings is not null</c>) but <paramref name="CanUseSsh"/> is still
/// false - i.e. the connectivity probe has not (yet, or ever) reported success. Distinct from "SSH
/// is not configured at all" (both false), which is a stable, permanent fact rather than a
/// transient one. <see cref="Services.PolicyResolver.Resolve"/> uses this to DEFER a fixable core
/// advisory to a later poll instead of resolving it as <see cref="SecSwitchAction.ShutdownCore"/> -
/// <c>CheckConfigurationHostedService.StartAsync</c> fires its probe without awaiting it, so a
/// SecSwitch poll running concurrently with that still-in-flight (or still-backing-off) probe must
/// not permanently mistake "not yet verified" for "not available", since
/// <c>SecSwitchMonitor.RecordAsync</c> would otherwise cache the wrong outcome as the TERMINAL
/// <c>Acted</c> status - which no later poll, however many, could ever correct back to the real
/// <see cref="SecSwitchAction.UpdateCore"/>. Defaults to false so every pre-existing call site
/// (including this plugin's own tests written before this parameter existed) keeps its original,
/// unambiguous meaning of "no SSH available, full stop".
/// </param>
public sealed record InstanceState(
    IReadOnlyDictionary<string, Version> InstalledPlugins,
    Version CoreVersion,
    bool CanUseSsh,
    bool SshVerificationPending = false);

public enum SecSwitchAction { None, Notify, UpdatePlugin, DisablePlugin, UpdateCore, ShutdownCore }

/// <param name="IntendedAction">
/// The action policy would have taken absent a downgrade (Manual mode, a notify-only pin, or the
/// severity gate) - populated only at those three downgrade sites in PolicyResolver.Resolve, where
/// <paramref name="Action"/> is Notify but something stronger was computed and set aside. Null
/// everywhere else, including when <paramref name="Action"/> already IS the intended action, so
/// there is never more than one source of truth for what the policy decided.
/// </param>
public sealed record PolicyDecision(SecSwitchAction Action, string Reason, SecSwitchAction? IntendedAction = null);
