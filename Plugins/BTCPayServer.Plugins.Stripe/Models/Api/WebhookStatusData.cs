#nullable enable
namespace BTCPayServer.Plugins.Stripe.Models.Api;

/// <summary>
/// Webhook status data returned by the Greenfield API.
/// </summary>
public class WebhookStatusData
{
    public bool Configured { get; set; }

    public string? WebhookId { get; set; }

    public string? WebhookUrl { get; set; }

    public string? Message { get; set; }
}
