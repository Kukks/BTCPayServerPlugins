using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class LightningTools
{
    private static string LnPath(string storeId, string cryptoCode, string scope) =>
        scope == "server"
            ? $"/api/v1/server/lightning/{cryptoCode}"
            : $"/api/v1/stores/{storeId}/lightning/{cryptoCode}";

    [McpServerTool(Name = "btcpay_lightning_info"),
     Description("Get lightning node info including alias, public key, chain sync status, and channel count")]
    public static async Task<string> GetInfo(
        McpHttpClient client,
        [Description("The store ID (required for store scope)")] string storeId,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' (store's lightning node) or 'server' (internal node)")] string scope = "store")
    {
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/info");
    }

    [McpServerTool(Name = "btcpay_lightning_balance"),
     Description("Get lightning node balance including on-chain and channel balances")]
    public static async Task<string> GetBalance(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/balance");
    }

    [McpServerTool(Name = "btcpay_lightning_channels"),
     Description("List all lightning channels")]
    public static async Task<string> ListChannels(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/channels");
    }

    [McpServerTool(Name = "btcpay_lightning_channel_open"),
     Description("Open a new lightning channel to a peer")]
    public static async Task<string> OpenChannel(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Node URI of the peer (pubkey@host:port)")] string nodeURI,
        [Description("Channel capacity in satoshis")] long channelAmount,
        [Description("Fee rate in sat/vB")] int? feeRate = null,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.PostAsync($"{LnPath(storeId, cryptoCode, scope)}/channels",
            new { nodeURI, channelAmount, feeRate });
    }

    [McpServerTool(Name = "btcpay_lightning_connect"),
     Description("Connect to a lightning peer without opening a channel")]
    public static async Task<string> ConnectToNode(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Node URI of the peer (pubkey@host:port)")] string nodeURI,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.PostAsync($"{LnPath(storeId, cryptoCode, scope)}/connect",
            new { nodeURI });
    }

    [McpServerTool(Name = "btcpay_lightning_deposit_address"),
     Description("Get an on-chain deposit address for the lightning node")]
    public static async Task<string> GetDepositAddress(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.PostAsync($"{LnPath(storeId, cryptoCode, scope)}/address");
    }

    [McpServerTool(Name = "btcpay_lightning_invoices_list"),
     Description("List lightning invoices")]
    public static async Task<string> ListInvoices(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Only show pending (unpaid) invoices")] bool? pendingOnly = null,
        [Description("Offset index for pagination")] long? offsetIndex = null,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        var q = new QueryBuilder();
        if (pendingOnly.HasValue) q.Add("pendingOnly", pendingOnly.Value.ToString().ToLower());
        if (offsetIndex.HasValue) q.Add("offsetIndex", offsetIndex.Value.ToString());
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/invoices{q}");
    }

    [McpServerTool(Name = "btcpay_lightning_invoice_get"),
     Description("Get a specific lightning invoice by ID")]
    public static async Task<string> GetInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The lightning invoice ID")] string id,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/invoices/{id}");
    }

    [McpServerTool(Name = "btcpay_lightning_invoice_create"),
     Description("Create a new lightning invoice (BOLT11 payment request)")]
    public static async Task<string> CreateInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Amount in millisatoshis (e.g. 10000 = 10 sats)")] long amount,
        [Description("Invoice description")] string? description = null,
        [Description("Description hash (hex) for BOLT11")] string? descriptionHashOnly = null,
        [Description("Expiry time in seconds")] int? expiry = null,
        [Description("Whether to include private route hints")] bool? privateRouteHints = null,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.PostAsync($"{LnPath(storeId, cryptoCode, scope)}/invoices",
            new { amount, description, descriptionHashOnly, expiry, privateRouteHints });
    }

    [McpServerTool(Name = "btcpay_lightning_pay"),
     Description("Pay a BOLT11 lightning invoice")]
    public static async Task<string> PayInvoice(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("BOLT11 payment request string")] string BOLT11,
        [Description("Maximum fee in millisatoshis")] long? maxFeeFlat = null,
        [Description("Maximum fee as percentage of payment amount")] double? maxFeePercent = null,
        [Description("Amount in millisatoshis (for zero-amount invoices)")] long? amount = null,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.PostAsync($"{LnPath(storeId, cryptoCode, scope)}/invoices/pay",
            new { BOLT11, maxFeeFlat, maxFeePercent, amount });
    }

    [McpServerTool(Name = "btcpay_lightning_payments_list"),
     Description("List lightning payments made from this node")]
    public static async Task<string> ListPayments(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Include pending (in-flight) payments")] bool? includePending = null,
        [Description("Offset index for pagination")] long? offsetIndex = null,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        var q = new QueryBuilder();
        if (includePending.HasValue) q.Add("includePending", includePending.Value.ToString().ToLower());
        if (offsetIndex.HasValue) q.Add("offsetIndex", offsetIndex.Value.ToString());
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/payments{q}");
    }

    [McpServerTool(Name = "btcpay_lightning_payment_get"),
     Description("Get details of a specific lightning payment by hash")]
    public static async Task<string> GetPayment(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment hash")] string paymentHash,
        [Description("Crypto code (e.g. BTC)")] string cryptoCode = "BTC",
        [Description("Scope: 'store' or 'server'")] string scope = "store")
    {
        return await client.GetAsync($"{LnPath(storeId, cryptoCode, scope)}/payments/{paymentHash}");
    }

    [McpServerTool(Name = "btcpay_lightning_addresses_list"),
     Description("List all lightning addresses configured for a store")]
    public static async Task<string> ListLightningAddresses(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/lightning-addresses");
    }

    [McpServerTool(Name = "btcpay_lightning_address_get"),
     Description("Get a specific lightning address configuration")]
    public static async Task<string> GetLightningAddress(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The lightning address username (the part before @)")] string username)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/lightning-addresses/{username}");
    }

    [McpServerTool(Name = "btcpay_lightning_address_set"),
     Description("Add or update a lightning address for a store")]
    public static async Task<string> SetLightningAddress(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The lightning address username")] string username,
        [Description("Maximum amount in satoshis (null for no limit)")] long? max = null,
        [Description("Minimum amount in satoshis")] long? min = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/lightning-addresses/{username}",
            new { max, min });
    }

    [McpServerTool(Name = "btcpay_lightning_address_remove"),
     Description("Remove a lightning address from a store")]
    public static async Task<string> RemoveLightningAddress(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The lightning address username")] string username)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/lightning-addresses/{username}");
    }
}
