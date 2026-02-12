using System.Threading.Tasks;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "btcpay_server_info"),
     Description("Get BTCPay Server info including version, network type (mainnet/testnet/signet), supported payment methods, and sync status")]
    public static async Task<string> GetServerInfo(McpHttpClient client)
    {
        return await client.GetAsync("/api/v1/server/info");
    }

    [McpServerTool(Name = "btcpay_server_health"),
     Description("Check if the BTCPay Server instance is healthy and responding")]
    public static async Task<string> GetServerHealth(McpHttpClient client)
    {
        return await client.GetAsync("/api/v1/health");
    }

    [McpServerTool(Name = "btcpay_rate_sources"),
     Description("List all available exchange rate sources that can be used for store rate configuration")]
    public static async Task<string> GetRateSources(McpHttpClient client)
    {
        return await client.GetAsync("/misc/rate-sources");
    }
}
