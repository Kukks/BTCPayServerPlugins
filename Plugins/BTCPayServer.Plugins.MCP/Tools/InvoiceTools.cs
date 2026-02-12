using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class InvoiceTools
{
    [McpServerTool(Name = "btcpay_invoices_list"),
     Description("List invoices for a store with optional filters for status, date range, order ID, and text search")]
    public static async Task<string> ListInvoices(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Filter by status (can specify multiple): New, Processing, Expired, Invalid, Settled")] string[]? status = null,
        [Description("Filter by order ID (can specify multiple)")] string[]? orderId = null,
        [Description("Only include invoices created after this date (ISO 8601)")] string? startDate = null,
        [Description("Only include invoices created before this date (ISO 8601)")] string? endDate = null,
        [Description("Full-text search across invoice fields")] string? textSearch = null,
        [Description("Include archived invoices")] bool includeArchived = false,
        [Description("Number of records to skip")] int? skip = null,
        [Description("Number of records to return (default 50)")] int? take = 50)
    {
        var q = new QueryBuilder();
        if (status != null) foreach (var s in status) q.Add("status", s);
        if (orderId != null) foreach (var o in orderId) q.Add("orderId", o);
        if (startDate != null) q.Add("startDate", startDate);
        if (endDate != null) q.Add("endDate", endDate);
        if (textSearch != null) q.Add("textSearch", textSearch);
        if (includeArchived) q.Add("includeArchived", "true");
        if (skip.HasValue) q.Add("skip", skip.Value.ToString());
        if (take.HasValue) q.Add("take", take.Value.ToString());
        return await client.GetAsync($"/api/v1/stores/{storeId}/invoices{q}");
    }

    [McpServerTool(Name = "btcpay_invoice_get"),
     Description("Get full details of a specific invoice including payment information")]
    public static async Task<string> GetInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}");
    }

    [McpServerTool(Name = "btcpay_invoice_create"),
     Description("Create a new payment invoice. Omit amount for a top-up invoice where the customer chooses the amount.")]
    public static async Task<string> CreateInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Invoice amount (omit for top-up invoice)")] decimal? amount = null,
        [Description("Currency code (e.g. USD, EUR, BTC). Defaults to store's default currency.")] string? currency = null,
        [Description("Your order/reference ID")] string? orderId = null,
        [Description("Buyer email address")] string? buyerEmail = null,
        [Description("URL to redirect customer after payment")] string? redirectUrl = null,
        [Description("Invoice expiration in minutes (overrides store default)")] long? expirationMinutes = null)
    {
        var body = new Dictionary<string, object?>();
        if (amount.HasValue) body["amount"] = amount.Value;
        if (currency != null) body["currency"] = currency;
        if (orderId != null) body["orderId"] = orderId;
        if (redirectUrl != null) body["redirectUrl"] = redirectUrl;
        if (expirationMinutes.HasValue) body["expirationMinutes"] = expirationMinutes.Value;
        if (buyerEmail != null) body["checkout"] = new { buyerEmail };
        return await client.PostAsync($"/api/v1/stores/{storeId}/invoices", body);
    }

    [McpServerTool(Name = "btcpay_invoice_update"),
     Description("Update invoice metadata (e.g. orderId, buyer info)")]
    public static async Task<string> UpdateInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId,
        [Description("Metadata key-value pairs as JSON")] string? metadata = null,
        [Description("Order ID")] string? orderId = null)
    {
        var body = new Dictionary<string, object?>();
        if (orderId != null) body["orderId"] = orderId;
        if (metadata != null) body["metadata"] = System.Text.Json.JsonSerializer.Deserialize<object>(metadata);
        return await client.PutAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}", body);
    }

    [McpServerTool(Name = "btcpay_invoice_archive"),
     Description("Archive an invoice (soft delete). Can be unarchived later.")]
    public static async Task<string> ArchiveInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}");
    }

    [McpServerTool(Name = "btcpay_invoice_unarchive"),
     Description("Restore a previously archived invoice")]
    public static async Task<string> UnarchiveInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}/unarchive");
    }

    [McpServerTool(Name = "btcpay_invoice_mark_status"),
     Description("Manually change an invoice's status (e.g. mark as settled or invalid)")]
    public static async Task<string> MarkInvoiceStatus(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId,
        [Description("New status: Settled, Invalid, or Processing")] string status)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}/status", new { status });
    }

    [McpServerTool(Name = "btcpay_invoice_payment_methods"),
     Description("Get all payment methods available for an invoice with payment details")]
    public static async Task<string> GetInvoicePaymentMethods(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId,
        [Description("Only include accounted payments")] bool onlyAccountedPayments = true)
    {
        var q = new QueryBuilder();
        if (!onlyAccountedPayments) q.Add("onlyAccountedPayments", "false");
        return await client.GetAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}/payment-methods{q}");
    }

    [McpServerTool(Name = "btcpay_invoice_activate_payment_method"),
     Description("Activate a specific payment method for an invoice")]
    public static async Task<string> ActivateInvoicePaymentMethod(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId,
        [Description("Payment method ID (e.g. BTC-CHAIN, BTC-LN)")] string paymentMethod)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}/payment-methods/{paymentMethod}/activate");
    }

    [McpServerTool(Name = "btcpay_invoice_refund"),
     Description("Create a refund for an invoice. Creates a pull payment for the refund amount.")]
    public static async Task<string> RefundInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The invoice ID")] string invoiceId,
        [Description("Payment method to refund with (e.g. BTC-CHAIN)")] string? paymentMethod = null,
        [Description("Refund variance percentage tolerance")] decimal? refundVariant = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/invoices/{invoiceId}/refund",
            new { paymentMethod, refundVariant });
    }
}
