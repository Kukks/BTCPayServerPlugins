#nullable enable
namespace BTCPayServer.Plugins.Stripe.Models.Api;

/// <summary>
/// Stripe settings data returned by the Greenfield API.
/// API keys are masked for security.
/// </summary>
public class StripeSettingsData
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Masked publishable key (e.g. pk_***...xyz)
    /// </summary>
    public string? PublishableKey { get; set; }

    /// <summary>
    /// Masked secret key (e.g. sk_***...xyz)
    /// </summary>
    public string? SecretKey { get; set; }

    public string SettlementCurrency { get; set; } = "USD";

    public string? AdvancedConfig { get; set; }

    public bool IsConfigured { get; set; }

    public bool IsTestMode { get; set; }

    /// <summary>
    /// Stripe webhook endpoint ID if configured
    /// </summary>
    public string? WebhookId { get; set; }
}
