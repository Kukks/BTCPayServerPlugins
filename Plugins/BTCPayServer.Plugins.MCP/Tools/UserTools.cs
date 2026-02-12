using System.Threading.Tasks;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class UserTools
{
    [McpServerTool(Name = "btcpay_user_me"),
     Description("Get information about the current authenticated user")]
    public static async Task<string> GetCurrentUser(McpHttpClient client)
    {
        return await client.GetAsync("/api/v1/users/me");
    }

    [McpServerTool(Name = "btcpay_users_list"),
     Description("List all users on the server (requires server admin permission)")]
    public static async Task<string> ListUsers(McpHttpClient client)
    {
        return await client.GetAsync("/api/v1/users");
    }

    [McpServerTool(Name = "btcpay_user_get"),
     Description("Get a specific user by ID or email (requires server admin permission)")]
    public static async Task<string> GetUser(
        McpHttpClient client,
        [Description("User ID or email address")] string idOrEmail)
    {
        return await client.GetAsync($"/api/v1/users/{idOrEmail}");
    }

    [McpServerTool(Name = "btcpay_user_create"),
     Description("Create a new user account (requires server admin permission)")]
    public static async Task<string> CreateUser(
        McpHttpClient client,
        [Description("Email address")] string email,
        [Description("Password")] string password,
        [Description("Make the user a server admin")] bool isAdministrator = false)
    {
        return await client.PostAsync("/api/v1/users", new { email, password, isAdministrator });
    }
}
