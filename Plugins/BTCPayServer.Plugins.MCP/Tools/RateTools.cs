using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class RateTools
{
    [McpServerTool(Name = "btcpay_rates_get"),
     Description("Get current exchange rates for a store. Returns rates for all configured currency pairs.")]
    public static async Task<string> GetRates(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Specific currency pairs to query (e.g. BTC_USD, BTC_EUR)")] string[]? currencyPair = null)
    {
        var q = new QueryBuilder();
        if (currencyPair != null) foreach (var p in currencyPair) q.Add("currencyPair", p);
        return await client.GetAsync($"/api/v1/stores/{storeId}/rates{q}");
    }

    [McpServerTool(Name = "btcpay_rates_config_get"),
     Description("Get the exchange rate configuration for a store")]
    public static async Task<string> GetRateConfig(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/rates/configuration");
    }

    [McpServerTool(Name = "btcpay_rates_config_update"),
     Description("Update the exchange rate configuration for a store")]
    public static async Task<string> UpdateRateConfig(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Rate source/provider name")] string? effectiveSource = null,
        [Description("Rate scripting rule")] string? script = null,
        [Description("Spread percentage to add")] decimal? spread = null,
        [Description("Whether scripting is enabled")] bool? isCustomScript = null)
    {
        return await client.PutAsync($"/api/v1/stores/{storeId}/rates/configuration",
            new { effectiveSource, script, spread, isCustomScript });
    }

    [McpServerTool(Name = "btcpay_rates_preview"),
     Description("Preview how a rate configuration would affect exchange rates without saving")]
    public static async Task<string> PreviewRateConfig(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Rate source to preview")] string? effectiveSource = null,
        [Description("Rate script to preview")] string? script = null,
        [Description("Spread percentage")] decimal? spread = null,
        [Description("Currency pairs to preview (e.g. BTC_USD)")] string[]? currencyPair = null)
    {
        var q = new QueryBuilder();
        if (currencyPair != null) foreach (var p in currencyPair) q.Add("currencyPair", p);
        return await client.PostAsync($"/api/v1/stores/{storeId}/rates/configuration/preview{q}",
            new { effectiveSource, script, spread });
    }
}
