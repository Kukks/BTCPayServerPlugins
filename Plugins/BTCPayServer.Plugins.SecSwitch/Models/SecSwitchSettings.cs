using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchSettings
{
    public bool Enabled { get; set; }
    public string FeedUrl { get; set; } = "https://kukks.github.io/secswitch-advisories/";
    public int QuorumThreshold { get; set; } = 2;
    public bool AutoApply { get; set; } = true;
    public bool SeverityGateEnabled { get; set; } = true;
    public bool PreferUpdateOverDisable { get; set; } = true;
    public List<TrustedKey> TrustedKeys { get; set; } = [];
    public List<string> NotifyOnlyIdentifiers { get; set; } = [];
}
