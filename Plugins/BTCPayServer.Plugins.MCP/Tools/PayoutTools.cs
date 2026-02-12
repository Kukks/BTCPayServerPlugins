using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class PayoutTools
{
    [McpServerTool(Name = "btcpay_pull_payments_list"),
     Description("List pull payments for a store. Pull payments allow external parties to claim payouts.")]
    public static async Task<string> ListPullPayments(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Include archived pull payments")] bool includeArchived = false)
    {
        var q = new QueryBuilder();
        if (includeArchived) q.Add("includeArchived", "true");
        return await client.GetAsync($"/api/v1/stores/{storeId}/pull-payments{q}");
    }

    [McpServerTool(Name = "btcpay_pull_payment_create"),
     Description("Create a new pull payment. This generates a link that recipients can use to claim payouts.")]
    public static async Task<string> CreatePullPayment(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Pull payment name")] string name,
        [Description("Total amount available for claiming")] decimal amount,
        [Description("Currency (e.g. BTC, USD)")] string currency,
        [Description("Allowed payment methods as JSON array (e.g. [\"BTC-CHAIN\",\"BTC-LN\"])")] string? paymentMethods = null,
        [Description("Expiry date (ISO 8601). Null for no expiry.")] string? expiresAt = null,
        [Description("Auto-approve payouts")] bool? autoApproveClaims = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["amount"] = amount,
            ["currency"] = currency,
            ["autoApproveClaims"] = autoApproveClaims
        };
        if (paymentMethods != null) body["paymentMethods"] = System.Text.Json.JsonSerializer.Deserialize<object>(paymentMethods);
        if (expiresAt != null) body["expiresAt"] = expiresAt;
        return await client.PostAsync($"/api/v1/stores/{storeId}/pull-payments", body);
    }

    [McpServerTool(Name = "btcpay_pull_payment_get"),
     Description("Get details of a specific pull payment")]
    public static async Task<string> GetPullPayment(
        McpHttpClient client,
        [Description("The pull payment ID")] string pullPaymentId)
    {
        return await client.GetAsync($"/api/v1/pull-payments/{pullPaymentId}");
    }

    [McpServerTool(Name = "btcpay_pull_payment_archive"),
     Description("Archive a pull payment (prevents new claims)")]
    public static async Task<string> ArchivePullPayment(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The pull payment ID")] string pullPaymentId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/pull-payments/{pullPaymentId}");
    }

    [McpServerTool(Name = "btcpay_payouts_list"),
     Description("List payouts for a store")]
    public static async Task<string> ListPayouts(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Include cancelled payouts")] bool includeCancelled = false)
    {
        var q = new QueryBuilder();
        if (includeCancelled) q.Add("includeCancelled", "true");
        return await client.GetAsync($"/api/v1/stores/{storeId}/payouts{q}");
    }

    [McpServerTool(Name = "btcpay_payout_get"),
     Description("Get details of a specific payout")]
    public static async Task<string> GetPayout(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payout ID")] string payoutId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/payouts/{payoutId}");
    }

    [McpServerTool(Name = "btcpay_payout_create"),
     Description("Create a payout directly from a store (not through a pull payment)")]
    public static async Task<string> CreatePayout(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Destination address or BOLT11 invoice")] string destination,
        [Description("Amount to pay")] decimal? amount = null,
        [Description("Payment method (e.g. BTC-CHAIN, BTC-LN)")] string? paymentMethod = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/payouts",
            new { destination, amount, paymentMethod });
    }

    [McpServerTool(Name = "btcpay_payout_approve"),
     Description("Approve a pending payout for processing")]
    public static async Task<string> ApprovePayout(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payout ID")] string payoutId)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/payouts/{payoutId}", new { });
    }

    [McpServerTool(Name = "btcpay_payout_cancel"),
     Description("Cancel a payout")]
    public static async Task<string> CancelPayout(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payout ID")] string payoutId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/payouts/{payoutId}");
    }

    [McpServerTool(Name = "btcpay_payout_mark_paid"),
     Description("Mark a payout as paid (for manual/external payments)")]
    public static async Task<string> MarkPayoutPaid(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payout ID")] string payoutId)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/payouts/{payoutId}/mark-paid");
    }
}
