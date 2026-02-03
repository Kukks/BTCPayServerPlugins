using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stripe.PaymentHandler;

/// <summary>
/// Customizes the checkout model for Stripe payments.
/// </summary>
public class StripeCheckoutModelExtension : ICheckoutModelExtension
{
    /// <summary>
    /// The name of the Vue component used for Stripe checkout.
    /// </summary>
    public const string CheckoutBodyComponentName = "StripeCheckout";

    public PaymentMethodId PaymentMethodId => StripePlugin.StripePaymentMethodId;
    public string Image { get; }

    // /// <summary>
    // /// Path to the Stripe payment method icon.
    // /// </summary>
    // public string Image => "Resources/stripe.svg";

    /// <summary>
    /// Optional badge text (empty for Stripe).
    /// </summary>
    public string Badge => "💳";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not StripePaymentMethodHandler handler)
            return;

        // Use custom Vue component for Stripe checkout
        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;

        // Parse the prompt details to get Stripe-specific data
        var promptDetails = (StripePromptDetails)handler.ParsePaymentPromptDetails(context.Prompt.Details);

        // Add Stripe-specific data to the checkout model
        context.Model.AdditionalData["stripePublishableKey"] = JToken.FromObject(promptDetails.PublishableKey);
        context.Model.AdditionalData["stripeClientSecret"] = JToken.FromObject(promptDetails.ClientSecret);
        context.Model.AdditionalData["stripePaymentIntentId"] = JToken.FromObject(promptDetails.PaymentIntentId);
        context.Model.AdditionalData["stripeAmount"] = JToken.FromObject(promptDetails.Amount);
        context.Model.AdditionalData["stripeCurrency"] = JToken.FromObject(promptDetails.Currency);

        // Set address to display the payment amount
        context.Model.Address = $"{promptDetails.Amount:F2} {promptDetails.Currency.ToUpperInvariant()}";

        // Hide Bitcoin-specific UI elements
        context.Model.ShowRecommendedFee = false;
    }
}
