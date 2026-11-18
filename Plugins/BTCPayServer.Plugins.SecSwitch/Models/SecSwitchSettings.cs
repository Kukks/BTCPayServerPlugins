using System;
using System.Linq;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed class SecSwitchSettings
{
    public const string ProposedFeedUrl = "https://btcpayserver.github.io/advisories/advisories.json";
    const string LegacyPlaceholderFeedUrl = "https://raw.githubusercontent.com/btcpayserver/advisories/master/advisories.json";

    public bool Enabled { get; set; } = true;
    public string? FeedUrl { get; set; }
    public bool AutoApply { get; set; } = true;
    public bool AutoUpdateCore { get; set; }
    public string[] AutoUpdatePlugins { get; set; } = [];

    public bool IsPluginAutoUpdateEnabled(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier) &&
        AutoUpdatePlugins?.Contains(identifier, StringComparer.OrdinalIgnoreCase) == true;

    public string? GetFeedUrl()
    {
        var url = FeedUrl?.Trim();
        return string.IsNullOrEmpty(url) || string.Equals(url, LegacyPlaceholderFeedUrl, StringComparison.Ordinal)
            ? null
            : url;
    }
}
