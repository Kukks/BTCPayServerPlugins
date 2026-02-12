using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class PaymentTools
{
    [McpServerTool(Name = "btcpay_payment_methods_list"),
     Description("List all payment methods configured for a store (e.g. BTC on-chain, Lightning, Liquid)")]
    public static async Task<string> ListPaymentMethods(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Only show enabled payment methods")] bool? onlyEnabled = null,
        [Description("Include full configuration details")] bool? includeConfig = null)
    {
        var q = new QueryBuilder();
        if (onlyEnabled.HasValue) q.Add("onlyEnabled", onlyEnabled.Value.ToString().ToLower());
        if (includeConfig.HasValue) q.Add("includeConfig", includeConfig.Value.ToString().ToLower());
        return await client.GetAsync($"/api/v1/stores/{storeId}/payment-methods{q}");
    }

    [McpServerTool(Name = "btcpay_payment_method_get"),
     Description("Get details of a specific payment method configuration")]
    public static async Task<string> GetPaymentMethod(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN, BTC-LN, LTC-CHAIN)")] string paymentMethodId,
        [Description("Include full configuration details")] bool? includeConfig = null)
    {
        var q = new QueryBuilder();
        if (includeConfig.HasValue) q.Add("includeConfig", includeConfig.Value.ToString().ToLower());
        return await client.GetAsync($"/api/v1/stores/{storeId}/payment-methods/{paymentMethodId}{q}");
    }

    [McpServerTool(Name = "btcpay_payment_method_update"),
     Description("Update a payment method configuration. The config format depends on the payment method type.")]
    public static async Task<string> UpdatePaymentMethod(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN, BTC-LN)")] string paymentMethodId,
        [Description("Whether this payment method is enabled")] bool? enabled = null,
        [Description("Payment method configuration as JSON string (format varies by type)")] string? config = null)
    {
        var body = new Dictionary<string, object?>();
        if (enabled.HasValue) body["enabled"] = enabled.Value;
        if (config != null) body["config"] = System.Text.Json.JsonSerializer.Deserialize<object>(config);
        return await client.PutAsync($"/api/v1/stores/{storeId}/payment-methods/{paymentMethodId}", body);
    }

    [McpServerTool(Name = "btcpay_payment_method_remove"),
     Description("Remove a payment method from a store")]
    public static async Task<string> RemovePaymentMethod(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN, BTC-LN)")] string paymentMethodId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/payment-methods/{paymentMethodId}");
    }
}
