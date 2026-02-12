using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class WebhookTools
{
    [McpServerTool(Name = "btcpay_webhooks_list"),
     Description("List all webhooks configured for a store")]
    public static async Task<string> ListWebhooks(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/webhooks");
    }

    [McpServerTool(Name = "btcpay_webhook_get"),
     Description("Get details of a specific webhook")]
    public static async Task<string> GetWebhook(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The webhook ID")] string webhookId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/webhooks/{webhookId}");
    }

    [McpServerTool(Name = "btcpay_webhook_create"),
     Description("Create a new webhook to receive event notifications")]
    public static async Task<string> CreateWebhook(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("URL that will receive webhook POST requests")] string url,
        [Description("Enable the webhook")] bool enabled = true,
        [Description("Automatically redeliver failed deliveries")] bool automaticRedelivery = true,
        [Description("Secret for HMAC signature verification")] string? secret = null,
        [Description("Subscribe to all events")] bool everything = true,
        [Description("Specific event types to subscribe to (if everything=false)")] string[]? specificEvents = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/webhooks", new
        {
            url,
            enabled,
            automaticRedelivery,
            secret,
            authorizedEvents = new { everything, specificEvents }
        });
    }

    [McpServerTool(Name = "btcpay_webhook_update"),
     Description("Update an existing webhook")]
    public static async Task<string> UpdateWebhook(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The webhook ID")] string webhookId,
        [Description("URL")] string? url = null,
        [Description("Enable the webhook")] bool? enabled = null,
        [Description("Automatically redeliver")] bool? automaticRedelivery = null,
        [Description("Secret")] string? secret = null)
    {
        var body = new Dictionary<string, object?>();
        if (url != null) body["url"] = url;
        if (enabled.HasValue) body["enabled"] = enabled.Value;
        if (automaticRedelivery.HasValue) body["automaticRedelivery"] = automaticRedelivery.Value;
        if (secret != null) body["secret"] = secret;
        return await client.PutAsync($"/api/v1/stores/{storeId}/webhooks/{webhookId}", body);
    }

    [McpServerTool(Name = "btcpay_webhook_delete"),
     Description("Delete a webhook")]
    public static async Task<string> DeleteWebhook(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The webhook ID")] string webhookId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/webhooks/{webhookId}");
    }

    [McpServerTool(Name = "btcpay_webhook_deliveries"),
     Description("List recent delivery attempts for a webhook")]
    public static async Task<string> ListDeliveries(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The webhook ID")] string webhookId,
        [Description("Max number of deliveries to return")] int? count = null)
    {
        var q = new QueryBuilder();
        if (count.HasValue) q.Add("count", count.Value.ToString());
        return await client.GetAsync($"/api/v1/stores/{storeId}/webhooks/{webhookId}/deliveries{q}");
    }

    [McpServerTool(Name = "btcpay_webhook_redeliver"),
     Description("Retry a failed webhook delivery")]
    public static async Task<string> RedeliverWebhook(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The webhook ID")] string webhookId,
        [Description("The delivery ID")] string deliveryId)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/webhooks/{webhookId}/deliveries/{deliveryId}/redeliver");
    }
}
