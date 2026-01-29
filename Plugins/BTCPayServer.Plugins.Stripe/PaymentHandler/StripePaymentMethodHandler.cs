#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Stripe.PaymentHandler;

/// <summary>
/// Payment method handler for Stripe payments.
/// Creates PaymentIntents and configures checkout prompts.
/// </summary>
public class StripePaymentMethodHandler : IPaymentMethodHandler
{
    private readonly StripeService _stripeService;
    private readonly ILogger<StripePaymentMethodHandler> _logger;

    public JsonSerializer Serializer { get; }
    public PaymentMethodId PaymentMethodId { get; }

    public StripePaymentMethodHandler(
        StripeService stripeService,
        ILogger<StripePaymentMethodHandler> logger)
    {
        _stripeService = stripeService;
        _logger = logger;

        PaymentMethodId = StripePlugin.StripePaymentMethodId;

        // Use default serializer without network-specific configuration
        (_, Serializer) = BlobSerializer.CreateSerializer(null as NBitcoin.Network);
    }

    /// <summary>
    /// Called before rate fetching. Sets up currency and divisibility.
    /// For Stripe, we use the configured settlement currency (fiat).
    /// </summary>
    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        var config = ParsePaymentMethodConfigInternal(context.PaymentMethodConfig);
        if (!config.Enabled || !config.IsConfigured)
        {
            context.State = null;
            return Task.CompletedTask;
        }

        // Set currency to the Stripe settlement currency
        context.Prompt.Currency = config.SettlementCurrency.ToUpperInvariant();
        // Store config for use in ConfigurePrompt
        context.State = new PrepareState { Config = config };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Configures the payment prompt with Stripe PaymentIntent details.
    /// </summary>
    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if(context.State == null)
        {
            throw new PaymentMethodUnavailableException("Stripe payment method is not prepared");
        }
        if (context.State is not PrepareState prepareState)
        {
            throw new PaymentMethodUnavailableException("Stripe payment method not properly initialized");
        }

        var config = prepareState.Config;
        if (!config.IsConfigured || !config.Enabled)
        {
            throw new PaymentMethodUnavailableException("Stripe is not configured for this store");
        }

        context.Prompt.Divisibility = StripeService.IsZeroDecimalCurrency(config.SettlementCurrency) ? 0 : 2;
        context.Prompt.PaymentMethodFee = 0m; // Stripe fees are handled by Stripe

        // Calculate amount due in the settlement currency
        var invoice = context.InvoiceEntity;

        // Get the rate from invoice currency to Stripe settlement currency
        var rate = invoice.GetRate(
            new BTCPayServer.Rating.CurrencyPair(config.SettlementCurrency, invoice.Currency));

        if (rate == 0)
        {
            throw new PaymentMethodUnavailableException(
                $"Cannot get rate for {config.SettlementCurrency}/{invoice.Currency}");
        }

        // Calculate amount in settlement currency
        var amountInSettlementCurrency = invoice.Price / rate;

        // Round to appropriate decimal places
        var divisibility = context.Prompt.Divisibility;
        amountInSettlementCurrency = Math.Round(amountInSettlementCurrency, divisibility);

        // Create Stripe PaymentIntent
        var paymentIntent = await _stripeService.CreatePaymentIntent(
            config,
            amountInSettlementCurrency,
            invoice.Id);

        // Store the prompt details
        var promptDetails = new StripePromptDetails
        {
            ClientSecret = paymentIntent.ClientSecret,
            PublishableKey = config.PublishableKey!,
            PaymentIntentId = paymentIntent.Id,
            Amount = amountInSettlementCurrency,
            Currency = config.SettlementCurrency
        };

        context.Prompt.Destination = paymentIntent.Id; // Use PaymentIntent ID as destination identifier
        context.Prompt.Details = JObject.FromObject(promptDetails, Serializer);

        // Track the PaymentIntent ID for payment detection
        context.TrackedDestinations.Add(paymentIntent.Id);
        context.AdditionalSearchTerms.Add(paymentIntent.Id);

        _logger.LogInformation(
            "Created Stripe prompt for invoice {InvoiceId}: PaymentIntent {PaymentIntentId}, Amount {Amount} {Currency}",
            invoice.Id, paymentIntent.Id, amountInSettlementCurrency, config.SettlementCurrency);
    }

    /// <summary>
    /// Called after invoice is saved. Can be used for additional setup.
    /// </summary>
    public Task AfterSavingInvoice(PaymentMethodContext context)
    {
        // No additional work needed after saving
        return Task.CompletedTask;
    }

    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<StripePromptDetails>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(StripePromptDetails)}");
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return ParsePaymentMethodConfigInternal(config);
    }

    private StripePaymentMethodConfig ParsePaymentMethodConfigInternal(JToken? config)
    {
        if (config == null)
            return new StripePaymentMethodConfig { Enabled = false };
        return config.ToObject<StripePaymentMethodConfig>(Serializer)
               ?? new StripePaymentMethodConfig { Enabled = false };
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<StripePaymentData>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(StripePaymentData)}");
    }

    /// <summary>
    /// Internal state for passing data between BeforeFetchingRates and ConfigurePrompt.
    /// </summary>
    private class PrepareState
    {
        public required StripePaymentMethodConfig Config { get; init; }
    }
}
