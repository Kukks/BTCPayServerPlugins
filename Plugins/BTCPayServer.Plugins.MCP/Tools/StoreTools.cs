using System.Threading.Tasks;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class StoreTools
{
    [McpServerTool(Name = "btcpay_stores_list"),
     Description("List all stores accessible to the current API key")]
    public static async Task<string> ListStores(McpHttpClient client)
    {
        return await client.GetAsync("/api/v1/stores");
    }

    [McpServerTool(Name = "btcpay_store_get"),
     Description("Get detailed settings for a specific store")]
    public static async Task<string> GetStore(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}");
    }

    [McpServerTool(Name = "btcpay_store_create"),
     Description("Create a new store")]
    public static async Task<string> CreateStore(
        McpHttpClient client,
        [Description("Store name")] string name,
        [Description("Default currency (e.g. USD, EUR, BTC)")] string? defaultCurrency = null,
        [Description("Default payment method ID")] string? defaultPaymentMethodId = null)
    {
        return await client.PostAsync("/api/v1/stores", new { name, defaultCurrency, defaultPaymentMethodId });
    }

    [McpServerTool(Name = "btcpay_store_update"),
     Description("Update store settings")]
    public static async Task<string> UpdateStore(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("New store name")] string? name = null,
        [Description("Default currency")] string? defaultCurrency = null,
        [Description("Invoice expiration in minutes")] int? invoiceExpiration = null,
        [Description("Payment tolerance percentage")] double? paymentTolerance = null)
    {
        return await client.PutAsync($"/api/v1/stores/{storeId}", new { name, defaultCurrency, invoiceExpiration, paymentTolerance });
    }

    [McpServerTool(Name = "btcpay_store_delete"),
     Description("Permanently delete a store. This action cannot be undone.")]
    public static async Task<string> DeleteStore(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}");
    }

    [McpServerTool(Name = "btcpay_store_roles"),
     Description("Get available roles for a store")]
    public static async Task<string> GetStoreRoles(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/roles");
    }

    [McpServerTool(Name = "btcpay_store_users_list"),
     Description("List all users who have access to a store")]
    public static async Task<string> ListStoreUsers(
        McpHttpClient client,
        [Description("The store ID")] string storeId)
    {
        return await client.GetAsync($"/api/v1/stores/{storeId}/users");
    }

    [McpServerTool(Name = "btcpay_store_user_add"),
     Description("Add a user to a store with a specific role")]
    public static async Task<string> AddStoreUser(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("User ID or email address")] string userId,
        [Description("Role to assign (e.g. Owner, Manager, Employee, Guest)")] string role)
    {
        return await client.PostAsync($"/api/v1/stores/{storeId}/users", new { userId, role });
    }

    [McpServerTool(Name = "btcpay_store_user_remove"),
     Description("Remove a user's access to a store")]
    public static async Task<string> RemoveStoreUser(
        McpHttpClient client,
        [Description("The store ID")] string storeId,
        [Description("User ID or email address")] string idOrEmail)
    {
        return await client.DeleteAsync($"/api/v1/stores/{storeId}/users/{idOrEmail}");
    }
}
