using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using ModelContextProtocol.Server;

namespace BTCPayServer.Plugins.MCP.Tools;

[McpServerToolType]
public static class NotificationTools
{
    [McpServerTool(Name = "btcpay_notifications_list"),
     Description("List notifications for the current user")]
    public static async Task<string> ListNotifications(
        McpHttpClient client,
        [Description("Filter: true=seen only, false=unseen only, null=all")] bool? seen = null,
        [Description("Number of records to skip")] int? skip = null,
        [Description("Number of records to return")] int? take = null)
    {
        var q = new QueryBuilder();
        if (seen.HasValue) q.Add("seen", seen.Value.ToString().ToLower());
        if (skip.HasValue) q.Add("skip", skip.Value.ToString());
        if (take.HasValue) q.Add("take", take.Value.ToString());
        return await client.GetAsync($"/api/v1/users/me/notifications{q}");
    }

    [McpServerTool(Name = "btcpay_notification_get"),
     Description("Get a specific notification")]
    public static async Task<string> GetNotification(
        McpHttpClient client,
        [Description("The notification ID")] string id)
    {
        return await client.GetAsync($"/api/v1/users/me/notifications/{id}");
    }

    [McpServerTool(Name = "btcpay_notification_update"),
     Description("Mark a notification as seen or unseen")]
    public static async Task<string> UpdateNotification(
        McpHttpClient client,
        [Description("The notification ID")] string id,
        [Description("Mark as seen (true) or unseen (false)")] bool seen)
    {
        return await client.PutAsync($"/api/v1/users/me/notifications/{id}", new { seen });
    }

    [McpServerTool(Name = "btcpay_notification_delete"),
     Description("Delete a notification")]
    public static async Task<string> DeleteNotification(
        McpHttpClient client,
        [Description("The notification ID")] string id)
    {
        return await client.DeleteAsync($"/api/v1/users/me/notifications/{id}");
    }
}
