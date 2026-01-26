#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using Microsoft.Extensions.Logging;
using Stripe;

namespace BTCPayServer.Plugins.Stripe.Services;

/// <summary>
/// Service for interacting with the Stripe API.
/// </summary>
public class StripeService
{
    private readonly ILogger<StripeService> _logger;

    // Zero-decimal currencies that don't need multiplication by 100
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA",
        "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF"
    };

    public StripeService(ILogger<StripeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Create a Stripe PaymentIntent for an invoice.
    /// </summary>
    public async Task<PaymentIntent> CreatePaymentIntent(
        StripePaymentMethodConfig config,
        decimal amount,
        string invoiceId,
        string? statementDescriptor = null,
        CancellationToken cancellationToken = default)
    {
        var client = new StripeClient(config.SecretKey);
        var service = new PaymentIntentService(client);

        var stripeAmount = ToStripeAmount(amount, config.SettlementCurrency);

        var options = new PaymentIntentCreateOptions
        {
            Amount = stripeAmount,
            Currency = config.SettlementCurrency.ToLowerInvariant(),
            Metadata = new Dictionary<string, string>
            {
                { "btcpay_invoice_id", invoiceId }
            },
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true
            }
        };

        // Set statement descriptor if provided
        var descriptor = statementDescriptor ?? config.StatementDescriptor;
        if (!string.IsNullOrEmpty(descriptor))
        {
            // Stripe limits to 22 characters, alphanumeric + some special chars
            options.StatementDescriptor = descriptor.Length > 22
                ? descriptor[..22]
                : descriptor;
        }

        var paymentIntent = await service.CreateAsync(options, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Created PaymentIntent {PaymentIntentId} for invoice {InvoiceId} - {Amount} {Currency}",
            paymentIntent.Id, invoiceId, amount, config.SettlementCurrency);

        return paymentIntent;
    }

    /// <summary>
    /// Get a PaymentIntent by ID.
    /// </summary>
    public async Task<PaymentIntent> GetPaymentIntent(
        StripePaymentMethodConfig config,
        string paymentIntentId,
        CancellationToken cancellationToken = default)
    {
        var client = new StripeClient(config.SecretKey);
        var service = new PaymentIntentService(client);
        
        return await service.GetAsync(paymentIntentId, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Cancel a PaymentIntent.
    /// </summary>
    public async Task<PaymentIntent?> CancelPaymentIntent(
        StripePaymentMethodConfig config,
        string paymentIntentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = new StripeClient(config.SecretKey);
            var service = new PaymentIntentService(client);
            var paymentIntent = await service.CancelAsync(paymentIntentId, cancellationToken: cancellationToken);

            _logger.LogInformation("Cancelled PaymentIntent {PaymentIntentId}", paymentIntentId);
            return paymentIntent;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "payment_intent_unexpected_state")
        {
            // PaymentIntent may already be cancelled or succeeded
            _logger.LogWarning("Could not cancel PaymentIntent {PaymentIntentId}: {Message}",
                paymentIntentId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Try to auto-register a webhook endpoint with Stripe.
    /// </summary>
    public async Task<WebhookRegistrationResult> TryRegisterWebhook(
        StripePaymentMethodConfig config,
        string storeId,
        string btcpayExternalUrl,
        CancellationToken cancellationToken = default)
    {
        // Check if BTCPay has a publicly accessible URL
        if (string.IsNullOrEmpty(btcpayExternalUrl) ||
            btcpayExternalUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            btcpayExternalUrl.Contains("127.0.0.1") ||
            btcpayExternalUrl.Contains("::1"))
        {
            return new WebhookRegistrationResult
            {
                Success = false,
                Reason = "No public URL detected. Stripe webhooks require a publicly accessible URL. " +
                         "Payment verification will use synchronous API calls instead."
            };
        }

        var client = new StripeClient(config.SecretKey);
        var webhookUrl = $"{btcpayExternalUrl.TrimEnd('/')}/plugins/stripe/webhook/{storeId}";

        try
        {
            // Check for existing webhook first
            var listService = new WebhookEndpointService(client);
            var existingWebhooks = await listService.ListAsync(new WebhookEndpointListOptions { Limit = 100 },
                cancellationToken: cancellationToken);

            var existing = existingWebhooks.Data.FirstOrDefault(w =>
                w.Url.Equals(webhookUrl, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return new WebhookRegistrationResult
                {
                    Success = true,
                    WebhookId = existing.Id,
                    // Note: Can't retrieve secret of existing webhook, user needs to get from Stripe dashboard
                    Message = "Existing webhook found. Please retrieve the signing secret from Stripe dashboard."
                };
            }

            // Create new webhook
            var createService = new WebhookEndpointService(client);
            var webhook = await createService.CreateAsync(new WebhookEndpointCreateOptions
            {
                Url = webhookUrl,
                EnabledEvents = new List<string>
                {
                    "payment_intent.succeeded",
                    "payment_intent.payment_failed",
                    "charge.refunded",
                    "charge.dispute.created"
                }
            }, cancellationToken: cancellationToken);

            return new WebhookRegistrationResult
            {
                Success = true,
                WebhookId = webhook.Id,
                WebhookSecret = webhook.Secret,
                Message = "Webhook auto-configured successfully"
            };
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Failed to auto-register Stripe webhook for store {StoreId}", storeId);
            return new WebhookRegistrationResult
            {
                Success = false,
                Reason = $"Stripe API error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Test that API keys are valid.
    /// </summary>
    public async Task<(bool Success, string? Error)> TestConnection(
        StripePaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = new StripeClient(config.SecretKey);
            var service = new BalanceService(client);
            await service.GetAsync(cancellationToken: cancellationToken);
            return (true, null);
        }
        catch (StripeException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Convert a decimal amount to Stripe's smallest currency unit.
    /// </summary>
    public static long ToStripeAmount(decimal amount, string currency)
    {
        if (ZeroDecimalCurrencies.Contains(currency))
        {
            return (long)Math.Round(amount);
        }
        return (long)Math.Round(amount * 100);
    }

    /// <summary>
    /// Convert from Stripe's smallest currency unit to decimal.
    /// </summary>
    public static decimal FromStripeAmount(long amount, string currency)
    {
        if (ZeroDecimalCurrencies.Contains(currency))
        {
            return amount;
        }
        return amount / 100m;
    }

    /// <summary>
    /// Check if a currency is zero-decimal.
    /// </summary>
    public static bool IsZeroDecimalCurrency(string currency)
    {
        return ZeroDecimalCurrencies.Contains(currency);
    }
}

/// <summary>
/// Result of attempting to register a webhook.
/// </summary>
public class WebhookRegistrationResult
{
    public bool Success { get; set; }
    public string? WebhookId { get; set; }
    public string? WebhookSecret { get; set; }
    public string? Message { get; set; }
    public string? Reason { get; set; }
}
