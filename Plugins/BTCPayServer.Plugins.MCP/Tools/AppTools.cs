using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class AppTools
{
    [McpServerTool(Name = "btcpay_apps_list"),
     Description("List all apps (Point of Sale, Crowdfund) for a specific store")]
    public static async Task<string> ListApps(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/apps");
    }

    [McpServerTool(Name = "btcpay_apps_list_all"),
     Description("List all apps across all stores accessible to the API key")]
    public static async Task<string> ListAllApps(McpHttpClient client)
    {
        return await client.GetAsync("/api/v1/apps");
    }

    [McpServerTool(Name = "btcpay_app_get"),
     Description("Get basic information about an app")]
    public static async Task<string> GetApp(
        McpHttpClient client,
        [Description("The app ID")] string appId)
    {
        return await client.GetAsync($"/api/v1/apps/{appId}");
    }

    [McpServerTool(Name = "btcpay_app_delete"),
     Description("Permanently delete an app. This action cannot be undone.")]
    public static async Task<string> DeleteApp(
        McpHttpClient client,
        [Description("The app ID")] string appId)
    {
        return await client.DeleteAsync($"/api/v1/apps/{appId}");
    }

    [McpServerTool(Name = "btcpay_app_sales"),
     Description("Get sales statistics for an app")]
    public static async Task<string> GetAppSales(
        McpHttpClient client,
        [Description("The app ID")] string appId,
        [Description("Number of days to include (default 7)")] int numberOfDays = 7)
    {
        var q = new QueryBuilder();
        q.Add("numberOfDays", numberOfDays.ToString());
        return await client.GetAsync($"/api/v1/apps/{appId}/sales{q}");
    }

    [McpServerTool(Name = "btcpay_app_top_items"),
     Description("Get top-selling items for an app")]
    public static async Task<string> GetAppTopItems(
        McpHttpClient client,
        [Description("The app ID")] string appId,
        [Description("Number of records to skip")] int offset = 0,
        [Description("Number of records to return")] int count = 10)
    {
        var q = new QueryBuilder();
        q.Add("offset", offset.ToString());
        q.Add("count", count.ToString());
        return await client.GetAsync($"/api/v1/apps/{appId}/top-items{q}");
    }

    [McpServerTool(Name = "btcpay_pos_get"),
     Description("Get Point of Sale app details including items and settings")]
    public static async Task<string> GetPosApp(
        McpHttpClient client,
        [Description("The POS app ID")] string appId)
    {
        return await client.GetAsync($"/api/v1/apps/pos/{appId}");
    }

    [McpServerTool(Name = "btcpay_pos_create"),
     Description("Create a new Point of Sale app")]
    public static async Task<string> CreatePosApp(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("App name")] string appName,
        [Description("App title displayed to customers")] string? title = null,
        [Description("Currency for the POS")] string? currency = null,
        [Description("Default view: Static, Cart, Light, Print")] string? defaultView = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/apps/pos",
            new { appName, title, currency, defaultView });
    }

    [McpServerTool(Name = "btcpay_pos_update"),
     Description("Update a Point of Sale app")]
    public static async Task<string> UpdatePosApp(
        McpHttpClient client,
        [Description("The POS app ID")] string appId,
        [Description("App title")] string? title = null,
        [Description("Currency")] string? currency = null,
        [Description("Default view")] string? defaultView = null)
    {
        var body = new Dictionary<string, object?>();
        if (title != null) body["title"] = title;
        if (currency != null) body["currency"] = currency;
        if (defaultView != null) body["defaultView"] = defaultView;
        return await client.PutAsync($"/api/v1/apps/pos/{appId}", body);
    }

    [McpServerTool(Name = "btcpay_crowdfund_get"),
     Description("Get Crowdfund app details including goals and perks")]
    public static async Task<string> GetCrowdfundApp(
        McpHttpClient client,
        [Description("The Crowdfund app ID")] string appId)
    {
        return await client.GetAsync($"/api/v1/apps/crowdfund/{appId}");
    }

    [McpServerTool(Name = "btcpay_crowdfund_create"),
     Description("Create a new Crowdfund app")]
    public static async Task<string> CreateCrowdfundApp(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("App name")] string appName,
        [Description("App title")] string? title = null,
        [Description("Crowdfund description")] string? description = null,
        [Description("Target amount to raise")] decimal? targetAmount = null,
        [Description("Target currency")] string? targetCurrency = null,
        [Description("End date (ISO 8601)")] string? endDate = null)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/apps/crowdfund",
            new { appName, title, description, targetAmount, targetCurrency, endDate });
    }
}
