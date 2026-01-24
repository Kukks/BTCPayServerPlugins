#nullable enable
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Stripe.PaymentHandler;

/// <summary>
/// Configuration stored when Stripe payment method is activated for a store.
/// Contains all Stripe API credentials and settings.
/// </summary>
public class StripePaymentMethodConfig
{

    /// <summary>
    /// Whether the payment method is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Stripe publishable API key (starts with pk_)
    /// </summary>
    public string? PublishableKey { get; set; }

    /// <summary>
    /// Stripe secret API key (starts with sk_)
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The currency code Stripe will charge in (e.g., "USD", "EUR")
    /// </summary>
    public string SettlementCurrency { get; set; } = "USD";

    /// <summary>
    /// Custom statement descriptor shown on customer bank statements
    /// </summary>
    public string? StatementDescriptor { get; set; }

    /// <summary>
    /// Enable Apple Pay in the Payment Element
    /// </summary>
    public bool EnableApplePay { get; set; } = true;

    /// <summary>
    /// Enable Google Pay in the Payment Element
    /// </summary>
    public bool EnableGooglePay { get; set; } = true;

    /// <summary>
    /// Stripe webhook endpoint ID (for managing/deleting)
    /// </summary>
    public string? WebhookId { get; set; }

    /// <summary>
    /// Stripe webhook signing secret
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Whether this is a test mode configuration (inferred from API key prefix)
    /// </summary>
    [JsonIgnore]
    public bool IsTestMode => SecretKey?.StartsWith("sk_test_") == true;

    /// <summary>
    /// Check if the settings have valid API keys configured
    /// </summary>
    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrEmpty(PublishableKey) && !string.IsNullOrEmpty(SecretKey);
}
