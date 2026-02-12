using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class WalletTools
{
    private static string WalletPath(string storeId, string paymentMethodId) =>
        $"/api/v1/stores/{storeId}/payment-methods/{paymentMethodId}/wallet";

    [McpServerTool(Name = "btcpay_wallet_overview"),
     Description("Get on-chain wallet balance overview including confirmed and unconfirmed amounts")]
    public static async Task<string> GetWalletOverview(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN, LTC-CHAIN). Defaults to BTC-CHAIN.")] string paymentMethodId = "BTC-CHAIN")
    {
        return await client.GetAsync(WalletPath(storeId, paymentMethodId));
    }

    [McpServerTool(Name = "btcpay_wallet_balance_histogram"),
     Description("Get wallet balance history over time")]
    public static async Task<string> GetWalletHistogram(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN",
        [Description("Histogram type (e.g. Week, Month, Year)")] string? type = null)
    {
        var q = new QueryBuilder();
        if (type != null) q.Add("type", type);
        return await client.GetAsync($"{WalletPath(storeId, paymentMethodId)}/histogram{q}");
    }

    [McpServerTool(Name = "btcpay_wallet_fee_rate"),
     Description("Get the current network fee rate estimate")]
    public static async Task<string> GetFeeRate(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN",
        [Description("Target number of blocks for confirmation")] int? blockTarget = null)
    {
        var q = new QueryBuilder();
        if (blockTarget.HasValue) q.Add("blockTarget", blockTarget.Value.ToString());
        return await client.GetAsync($"{WalletPath(storeId, paymentMethodId)}/feerate{q}");
    }

    [McpServerTool(Name = "btcpay_wallet_address_new"),
     Description("Get a new unused receive address for the wallet")]
    public static async Task<string> GetNewAddress(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN",
        [Description("Force generate a new address even if the previous one was unused")] bool forceGenerate = false)
    {
        var q = new QueryBuilder();
        if (forceGenerate) q.Add("forceGenerate", "true");
        return await client.GetAsync($"{WalletPath(storeId, paymentMethodId)}/address{q}");
    }

    [McpServerTool(Name = "btcpay_wallet_address_unreserve"),
     Description("Un-reserve the last generated receive address")]
    public static async Task<string> UnreserveAddress(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN")
    {
        return await client.DeleteAsync($"{WalletPath(storeId, paymentMethodId)}/address");
    }

    [McpServerTool(Name = "btcpay_wallet_transactions_list"),
     Description("List wallet transactions with optional filters")]
    public static async Task<string> ListTransactions(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN",
        [Description("Filter by status: Confirmed, Unconfirmed, Replaced")] string[]? statusFilter = null,
        [Description("Filter by label")] string? labelFilter = null,
        [Description("Number of records to skip")] int skip = 0,
        [Description("Maximum number of records to return")] int limit = 50)
    {
        var q = new QueryBuilder();
        if (statusFilter != null) foreach (var s in statusFilter) q.Add("statusFilter", s);
        if (labelFilter != null) q.Add("labelFilter", labelFilter);
        q.Add("skip", skip.ToString());
        q.Add("limit", limit.ToString());
        return await client.GetAsync($"{WalletPath(storeId, paymentMethodId)}/transactions{q}");
    }

    [McpServerTool(Name = "btcpay_wallet_transaction_get"),
     Description("Get details of a specific wallet transaction")]
    public static async Task<string> GetTransaction(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The transaction ID")] string transactionId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN")
    {
        return await client.GetAsync($"{WalletPath(storeId, paymentMethodId)}/transactions/{transactionId}");
    }

    [McpServerTool(Name = "btcpay_wallet_transaction_create"),
     Description("Create and optionally sign a new on-chain transaction")]
    public static async Task<string> CreateTransaction(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Transaction destinations as JSON array: [{\"destination\": \"address\", \"amount\": \"0.01\"}]")] string destinations,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN",
        [Description("Fee rate in sat/vB")] decimal? feeRate = null,
        [Description("Whether to attempt broadcasting after creation")] bool proceedWithBroadcast = true,
        [Description("Don't spend unconfirmed UTXOs")] bool noChange = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["destinations"] = System.Text.Json.JsonSerializer.Deserialize<object>(destinations),
            ["proceedWithBroadcast"] = proceedWithBroadcast,
            ["noChange"] = noChange
        };
        if (feeRate.HasValue) body["feeRate"] = feeRate.Value;
        return await client.PostAsync($"{WalletPath(storeId, paymentMethodId)}/transactions", body);
    }

    [McpServerTool(Name = "btcpay_wallet_transaction_patch"),
     Description("Update transaction labels or comments")]
    public static async Task<string> PatchTransaction(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("The transaction ID")] string transactionId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN",
        [Description("Comment to add to the transaction")] string? comment = null,
        [Description("Labels as JSON array of objects")] string? labels = null)
    {
        var body = new Dictionary<string, object?>();
        if (comment != null) body["comment"] = comment;
        if (labels != null) body["labels"] = System.Text.Json.JsonSerializer.Deserialize<object>(labels);
        return await client.PatchAsync($"{WalletPath(storeId, paymentMethodId)}/transactions/{transactionId}", body);
    }

    [McpServerTool(Name = "btcpay_wallet_utxos"),
     Description("List all unspent transaction outputs (UTXOs) in the wallet")]
    public static async Task<string> ListUtxos(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("Payment method ID (e.g. BTC-CHAIN)")] string paymentMethodId = "BTC-CHAIN")
    {
        return await client.GetAsync($"{WalletPath(storeId, paymentMethodId)}/utxos");
    }
}
