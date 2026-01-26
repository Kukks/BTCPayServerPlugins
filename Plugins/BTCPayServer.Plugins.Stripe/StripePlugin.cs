#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using BTCPayServer.Plugins.Stripe.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Stripe;

/// <summary>
/// BTCPay Server plugin that adds Stripe as a payment method.
/// </summary>
public class StripePlugin : BaseBTCPayServerPlugin
{
    /// <summary>
    /// The payment method ID for Stripe payments.
    /// </summary>
    public static readonly PaymentMethodId StripePaymentMethodId = new("STRIPE");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // Register core services
        services.AddSingleton<StripeService>();

        // Register payment method handler
        services.AddSingleton<IPaymentMethodHandler, StripePaymentMethodHandler>();

        // Register checkout model extension
        services.AddSingleton<ICheckoutModelExtension, StripeCheckoutModelExtension>();

        // Register event listener for invoice lifecycle management
        services.AddHostedService<StripeInvoiceListener>();

        // Register UI extensions
        services.AddUIExtension("store-wallets-nav", "Stripe/StoreNavExtension");
        services.AddUIExtension("checkout-end", "Stripe/CheckoutPaymentMethodExtension");
        services.AddUIExtension("store-invoices-payments", "Stripe/ViewStripePaymentData");

        // Set display name for the payment method
        services.AddDefaultPrettyName(StripePaymentMethodId, "Stripe");

        base.Execute(services);
    }
}
