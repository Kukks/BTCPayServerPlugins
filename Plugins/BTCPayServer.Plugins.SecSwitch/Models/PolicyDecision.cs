using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed record InstanceState(
    IReadOnlyDictionary<string, Version> InstalledPlugins,
    Version? CoreVersion,
    bool CanUpdateCore);

public enum SecSwitchAction { None, Notify, UpdatePlugin, DisablePlugin, UpdateCore, Lockdown }

public sealed record PolicyDecision(SecSwitchAction Action, string Reason, SecSwitchAction? IntendedAction = null);
