using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ModelBinding;

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
    [BindNever]
    public Guid? PolicyRevision { get; set; }

    public bool IsPluginAutoUpdateEnabled(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier) &&
        AutoUpdatePlugins?.Contains(identifier, StringComparer.OrdinalIgnoreCase) == true;

    public bool HasLegacyPlaceholderFeedUrl() =>
        string.Equals(FeedUrl?.Trim(), LegacyPlaceholderFeedUrl, StringComparison.Ordinal);

    public string? GetFeedUrl()
    {
        var url = FeedUrl?.Trim();
        return string.IsNullOrEmpty(url) || HasLegacyPlaceholderFeedUrl() ? null : url;
    }
}
