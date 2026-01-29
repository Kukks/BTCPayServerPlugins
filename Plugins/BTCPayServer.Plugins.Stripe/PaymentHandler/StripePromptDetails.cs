namespace BTCPayServer.Plugins.Stripe.PaymentHandler;

/// <summary>
/// Details passed to the checkout UI for rendering Stripe Payment Element.
/// </summary>
public class StripePromptDetails
{
    /// <summary>
    /// Stripe PaymentIntent client secret for Stripe.js
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Stripe publishable key for initializing Stripe.js
    /// </summary>
    public string PublishableKey { get; set; } = string.Empty;

    /// <summary>
    /// PaymentIntent ID for tracking and cancellation
    /// </summary>
    public string PaymentIntentId { get; set; } = string.Empty;

    /// <summary>
    /// Amount to display (in settlement currency)
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Settlement currency code
    /// </summary>
    public string Currency { get; set; } = string.Empty;
}
