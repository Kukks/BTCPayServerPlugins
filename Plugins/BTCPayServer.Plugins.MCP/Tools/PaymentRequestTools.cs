using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class PaymentRequestTools
{
    [McpServerTool(Name = "btcpay_payment_requests_list"),
     Description("List payment requests for a store. Payment requests are shareable payment links with optional amounts.")]
    public static async Task<string> ListPaymentRequests(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Include archived payment requests")] bool includeArchived = false)
    {
        var q = new QueryBuilder();
        if (includeArchived) q.Add("includeArchived", "true");
        return await client.GetAsync($"/api/v1/stores/{storeId}/payment-requests{q}");
    }

    [McpServerTool(Name = "btcpay_payment_request_get"),
     Description("Get details of a specific payment request")]
    public static async Task<string> GetPaymentRequest(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payment request ID")] string paymentRequestId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/payment-requests/{paymentRequestId}");
    }

    [McpServerTool(Name = "btcpay_payment_request_create"),
     Description("Create a new payment request (shareable payment link)")]
    public static async Task<string> CreatePaymentRequest(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Title of the payment request")] string title,
        [Description("Amount to request")] decimal amount,
        [Description("Currency (e.g. USD, BTC)")] string currency,
        [Description("Description or notes")] string? description = null,
        [Description("Email to notify on payment")] string? email = null,
        [Description("Expiry date (ISO 8601)")] string? expiryDate = null,
        [Description("Allow payer to create multiple invoices")] bool? allowCustomPaymentAmounts = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/payment-requests",
            new { title, amount, currency, description, email, expiryDate, allowCustomPaymentAmounts });
    }

    [McpServerTool(Name = "btcpay_payment_request_update"),
     Description("Update an existing payment request")]
    public static async Task<string> UpdatePaymentRequest(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payment request ID")] string paymentRequestId,
        [Description("Title")] string? title = null,
        [Description("Amount")] decimal? amount = null,
        [Description("Currency")] string? currency = null,
        [Description("Description")] string? description = null,
        [Description("Email")] string? email = null)
    {
        var body = new Dictionary<string, object?>();
        if (title != null) body["title"] = title;
        if (amount.HasValue) body["amount"] = amount.Value;
        if (currency != null) body["currency"] = currency;
        if (description != null) body["description"] = description;
        if (email != null) body["email"] = email;
        return await client.PutAsync($"/api/v1/stores/{storeId}/payment-requests/{paymentRequestId}", body);
    }

    [McpServerTool(Name = "btcpay_payment_request_archive"),
     Description("Archive a payment request")]
    public static async Task<string> ArchivePaymentRequest(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payment request ID")] string paymentRequestId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/payment-requests/{paymentRequestId}");
    }

    [McpServerTool(Name = "btcpay_payment_request_pay"),
     Description("Create an invoice to pay a payment request")]
    public static async Task<string> PayPaymentRequest(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The payment request ID")] string paymentRequestId,
        [Description("Amount to pay (for partial payments)")] decimal? amount = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/payment-requests/{paymentRequestId}/pay",
            amount.HasValue ? new { amount = amount.Value } : null);
    }
}
