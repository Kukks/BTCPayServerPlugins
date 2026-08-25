using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed record InstanceState(
    IReadOnlyDictionary<string, Version> InstalledPlugins,
    Version CoreVersion,
    bool CanUseSsh);

public enum SecSwitchAction { None, Notify, UpdatePlugin, DisablePlugin, UpdateCore, ShutdownCore }

public sealed record PolicyDecision(SecSwitchAction Action, string Reason);
