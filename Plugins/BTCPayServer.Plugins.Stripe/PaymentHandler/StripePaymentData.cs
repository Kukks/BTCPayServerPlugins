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
    /// Amount received in smallest currency unit (e.g., cents)
    /// </summary>
    public long AmountReceived { get; set; }

    /// <summary>
    /// Currency code (e.g., "usd")
    /// </summary>
    public string Currency { get; set; } = string.Empty;
}
