using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

/// <param name="InstalledPlugins">
/// Installed plugin identifiers mapped to their installed version. Identifier lookups against
/// this map are treated as case-insensitive by AdvisoryApplicability.IsApplicable regardless of
/// which string comparer this dictionary itself was constructed with - callers do not need to
/// use StringComparer.OrdinalIgnoreCase for correctness, though core's own plugin service does.
/// </param>
public sealed record InstanceState(
    IReadOnlyDictionary<string, Version> InstalledPlugins,
    Version CoreVersion,
    bool CanUseSsh);

public enum SecSwitchAction { None, Notify, UpdatePlugin, DisablePlugin, UpdateCore, ShutdownCore }

/// <param name="IntendedAction">
/// The action policy would have taken absent a downgrade (Manual mode, a notify-only pin, or the
/// severity gate) - populated only at those three downgrade sites in PolicyResolver.Resolve, where
/// <paramref name="Action"/> is Notify but something stronger was computed and set aside. Null
/// everywhere else, including when <paramref name="Action"/> already IS the intended action, so
/// there is never more than one source of truth for what the policy decided.
/// </param>
public sealed record PolicyDecision(SecSwitchAction Action, string Reason, SecSwitchAction? IntendedAction = null);
