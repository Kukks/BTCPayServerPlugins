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

        // Set statement descriptor if provided and valid
        var descriptor = statementDescriptor ?? config.StatementDescriptor;
        var sanitizedDescriptor = SanitizeStatementDescriptor(descriptor);
        if (!string.IsNullOrEmpty(sanitizedDescriptor))
        {
            options.StatementDescriptor = sanitizedDescriptor;
        }

        // Apply advanced config overrides if provided
        ApplyAdvancedConfig(options, config);

        var paymentIntent = await service.CreateAsync(options, cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Created PaymentIntent {PaymentIntentId} for invoice {InvoiceId} - {Amount} {Currency}",
            paymentIntent.Id, invoiceId, amount, config.SettlementCurrency);

        return paymentIntent;
    }

    /// <summary>
    /// Apply advanced JSON config overrides to PaymentIntent options.
    /// </summary>
    private void ApplyAdvancedConfig(PaymentIntentCreateOptions options, StripePaymentMethodConfig config)
    {
        var advancedConfig = config.GetAdvancedConfigJson();
        if (advancedConfig == null)
            return;

        try
        {
            // Merge metadata
            if (advancedConfig["metadata"] is Newtonsoft.Json.Linq.JObject metadata)
            {
                foreach (var prop in metadata.Properties())
                {
                    options.Metadata[prop.Name] = prop.Value?.ToString() ?? "";
                }
            }

            // Override payment_method_types (disables automatic_payment_methods)
            if (advancedConfig["payment_method_types"] is Newtonsoft.Json.Linq.JArray paymentMethodTypes)
            {
                options.AutomaticPaymentMethods = null;
                options.PaymentMethodTypes = paymentMethodTypes.Select(t => t.ToString()).ToList();
            }

            // Override statement descriptor suffix (same sanitization rules apply)
            if (advancedConfig["statement_descriptor_suffix"]?.ToString() is { } suffix)
            {
                var sanitizedSuffix = SanitizeStatementDescriptor(suffix);
                if (!string.IsNullOrEmpty(sanitizedSuffix))
                {
                    options.StatementDescriptorSuffix = sanitizedSuffix;
                }
            }

            // Override capture method
            if (advancedConfig["capture_method"]?.ToString() is { } captureMethod && !string.IsNullOrEmpty(captureMethod))
            {
                options.CaptureMethod = captureMethod;
            }

            // Override setup_future_usage
            if (advancedConfig["setup_future_usage"]?.ToString() is { } futureUsage && !string.IsNullOrEmpty(futureUsage))
            {
                options.SetupFutureUsage = futureUsage;
            }

            _logger.LogDebug("Applied advanced config overrides for PaymentIntent");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply advanced config overrides");
        }
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
    /// Check the status of a configured webhook.
    /// </summary>
    public async Task<WebhookStatus> GetWebhookStatus(
        StripePaymentMethodConfig config,
        string storeId,
        string btcpayExternalUrl,
        CancellationToken cancellationToken = default)
    {
        var expectedUrl = $"{btcpayExternalUrl.TrimEnd('/')}/plugins/stripe/webhook/{storeId}";

        // No webhook ID configured
        if (string.IsNullOrEmpty(config.WebhookId))
        {
            return new WebhookStatus
            {
                IsConfigured = false,
                IsValid = false,
                Status = "Not configured",
                HasSigningSecret = !string.IsNullOrEmpty(config.WebhookSecret)
            };
        }

        try
        {
            var client = new StripeClient(config.SecretKey);
            var service = new WebhookEndpointService(client);
            var webhook = await service.GetAsync(config.WebhookId, cancellationToken: cancellationToken);

            var urlMatches = webhook.Url.Equals(expectedUrl, StringComparison.OrdinalIgnoreCase);

            return new WebhookStatus
            {
                IsConfigured = true,
                IsValid = webhook.Status == "enabled" && urlMatches,
                WebhookId = webhook.Id,
                WebhookUrl = webhook.Url,
                Status = webhook.Status == "enabled"
                    ? (urlMatches ? "Active" : "URL mismatch")
                    : webhook.Status,
                HasSigningSecret = !string.IsNullOrEmpty(config.WebhookSecret),
                Error = !urlMatches ? $"Expected URL: {expectedUrl}" : null
            };
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            return new WebhookStatus
            {
                IsConfigured = true,
                IsValid = false,
                WebhookId = config.WebhookId,
                Status = "Not found in Stripe",
                Error = "Webhook was deleted from Stripe. Please re-register.",
                HasSigningSecret = !string.IsNullOrEmpty(config.WebhookSecret)
            };
        }
        catch (StripeException ex)
        {
            return new WebhookStatus
            {
                IsConfigured = true,
                IsValid = false,
                WebhookId = config.WebhookId,
                Status = "Error",
                Error = ex.Message,
                HasSigningSecret = !string.IsNullOrEmpty(config.WebhookSecret)
            };
        }
    }

    /// <summary>
    /// Delete a webhook endpoint from Stripe.
    /// </summary>
    public async Task DeleteWebhook(
        StripePaymentMethodConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(config.WebhookId))
            return;

        try
        {
            var client = new StripeClient(config.SecretKey);
            var service = new WebhookEndpointService(client);
            await service.DeleteAsync(config.WebhookId, cancellationToken: cancellationToken);
            _logger.LogInformation("Deleted Stripe webhook {WebhookId}", config.WebhookId);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            // Webhook already deleted, that's fine
            _logger.LogDebug("Webhook {WebhookId} already deleted from Stripe", config.WebhookId);
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

    /// <summary>
    /// Sanitize a statement descriptor for Stripe.
    /// Stripe requires: 5-22 chars, alphanumeric + . * - ' " _ and space only.
    /// Cannot be only numbers.
    /// </summary>
    private static string? SanitizeStatementDescriptor(string? descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
            return null;

        // Remove invalid characters, keep only allowed: alphanumeric, space, . * - ' " _
        var sanitized = new System.Text.StringBuilder();
        foreach (var c in descriptor)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '*' ||
                c == '-' || c == '\'' || c == '"' || c == '_')
            {
                sanitized.Append(c);
            }
        }

        var result = sanitized.ToString().Trim();

        // Must be at least 5 characters
        if (result.Length < 5)
            return null;

        // Truncate to 22 characters max
        if (result.Length > 22)
            result = result[..22].TrimEnd();

        // Cannot consist solely of numbers
        if (result.All(char.IsDigit))
            return null;

        return result;
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

/// <summary>
/// Status of webhook configuration.
/// </summary>
public class WebhookStatus
{
    public bool IsConfigured { get; set; }
    public bool IsValid { get; set; }
    public string? WebhookId { get; set; }
    public string? WebhookUrl { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public bool HasSigningSecret { get; set; }
}
