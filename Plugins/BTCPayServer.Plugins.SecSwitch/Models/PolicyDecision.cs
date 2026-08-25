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

public sealed record PolicyDecision(SecSwitchAction Action, string Reason);
