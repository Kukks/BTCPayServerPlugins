namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchSettings
{
    public const string DefaultFeedUrl = "https://raw.githubusercontent.com/btcpayserver/advisories/master/advisories.json";

    public bool Enabled { get; set; }
    public string FeedUrl { get; set; } = DefaultFeedUrl;
    public bool AutoApply { get; set; } = true;
    public bool PreferUpdateOverDisable { get; set; } = true;
    public bool AutoUpdateAll { get; set; }
}
