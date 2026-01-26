namespace BTCPayServer.Plugins.Stripe.PaymentHandler;

/// <summary>
/// Data recorded when a Stripe payment completes.
/// </summary>
public class StripePaymentData
{
    /// <summary>
    /// Stripe PaymentIntent ID
    /// </summary>
    public string PaymentIntentId { get; set; } = string.Empty;

    /// <summary>
    /// Stripe Charge ID
    /// </summary>
    public string? ChargeId { get; set; }

    /// <summary>
    /// Amount received in smallest currency unit (e.g., cents)
    /// </summary>
    public long AmountReceived { get; set; }

    /// <summary>
    /// Currency code (e.g., "usd")
    /// </summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Payment method type used (e.g., "card", "apple_pay", "google_pay")
    /// </summary>
    public string? PaymentMethodType { get; set; }

    /// <summary>
    /// Last 4 digits of the card (if card payment)
    /// </summary>
    public string? Last4 { get; set; }

    /// <summary>
    /// Card brand (e.g., "visa", "mastercard")
    /// </summary>
    public string? Brand { get; set; }
}
