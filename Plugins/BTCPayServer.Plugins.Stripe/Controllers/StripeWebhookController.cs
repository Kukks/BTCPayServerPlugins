#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using BTCPayServer.Plugins.Stripe.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Events;

namespace BTCPayServer.Plugins.Stripe.Controllers;

/// <summary>
/// Controller for handling Stripe webhook events.
/// </summary>
[Route("plugins/stripe/webhook")]
[ApiController]
public class StripeWebhookController : ControllerBase
{
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly EventAggregator _aggregator;

    public StripeWebhookController(
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        ILogger<StripeWebhookController> logger,
        EventAggregator aggregator)
    {
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _handlers = handlers;
        _logger = logger;
        _aggregator = aggregator;
    }

    private StripePaymentMethodConfig? GetConfig(StoreData store)
    {
        return store.GetPaymentMethodConfig<StripePaymentMethodConfig>(
            StripePlugin.StripePaymentMethodId, _handlers);
    }

    [HttpPost("{storeId}")]
    public async Task<IActionResult> HandleWebhook(string storeId)
    {
        _logger.LogDebug("Received Stripe webhook request for store path: {StoreId}", storeId);

        var store = await _storeRepository.FindStore(storeId);
        if (store == null)
        {
            _logger.LogWarning(
                "Received webhook for unknown store {StoreId}. Verify the webhook URL in Stripe dashboard matches the store ID.",
                storeId);
            return BadRequest("Store not found");
        }

        _logger.LogDebug("Found store {StoreName} ({StoreId}) for webhook", store.StoreName, store.Id);

        var config = GetConfig(store);
        if (config == null || !config.IsConfigured)
        {
            _logger.LogWarning("Received webhook for unconfigured store {StoreId}", storeId);
            return BadRequest("Store not configured for Stripe");
        }

        // Read the raw body for signature verification
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        _logger.LogDebug(
            "Webhook body received. Length: {Length}, Has WebhookSecret: {HasSecret}",
            json.Length,
            !string.IsNullOrEmpty(config.WebhookSecret));

        Event stripeEvent;

        // Verify webhook signature if secret is configured
        if (!string.IsNullOrEmpty(config.WebhookSecret))
        {
            try
            {
                var signatureHeader = Request.Headers["Stripe-Signature"].FirstOrDefault();
                if (string.IsNullOrEmpty(signatureHeader))
                {
                    _logger.LogWarning("Missing Stripe-Signature header for store {StoreId}", storeId);
                    return BadRequest("Missing signature");
                }

                _logger.LogDebug(
                    "Verifying webhook signature for store {StoreId}. Secret prefix: {SecretPrefix}, Signature header: {SignatureHeader}, Body length: {BodyLength}",
                    storeId,
                    config.WebhookSecret.Length > 10 ? config.WebhookSecret[..10] + "..." : "[short]",
                    signatureHeader.Length > 50 ? signatureHeader[..50] + "..." : signatureHeader,
                    json.Length);

                // Allow API version mismatch - webhook may use older API version than Stripe.net library
                stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, config.WebhookSecret, throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid webhook signature for store {StoreId}. Error: {Error}. Secret configured: {HasSecret} (prefix: {SecretPrefix})",
                    storeId,
                    ex.Message,
                    !string.IsNullOrEmpty(config.WebhookSecret),
                    !string.IsNullOrEmpty(config.WebhookSecret) && config.WebhookSecret.Length > 10
                        ? config.WebhookSecret[..10] + "..."
                        : "[short/empty]");
                return BadRequest("Invalid signature");
            }
        }
        else
        {
            // No webhook secret configured, parse without verification
            try
            {
                // Allow API version mismatch - webhook may use older API version than Stripe.net library
                stripeEvent = EventUtility.ParseEvent(json, throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Failed to parse webhook event for store {StoreId}", storeId);
                return BadRequest("Invalid event");
            }
        }

        _logger.LogInformation(
            "Received Stripe webhook {EventType} ({EventId}) for store {StoreId}",
            stripeEvent.Type, stripeEvent.Id, storeId);

        try
        {
            switch (stripeEvent.Type)
            {
                case EventTypes.PaymentIntentSucceeded:
                    await HandlePaymentIntentSucceeded(storeId, config, stripeEvent);
                    break;

                case EventTypes.PaymentIntentPaymentFailed:
                    await HandlePaymentIntentFailed(storeId, stripeEvent);
                    break;

                case EventTypes.ChargeRefunded:
                    await HandleChargeRefunded(storeId, stripeEvent);
                    break;

                case EventTypes.ChargeDisputeCreated:
                    await HandleDisputeCreated(storeId, stripeEvent);
                    break;

                default:
                    _logger.LogDebug("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe event {EventId}", stripeEvent.Id);
            // Return 200 to acknowledge receipt (Stripe will retry otherwise)
        }

        return Ok();
    }

    private async Task HandlePaymentIntentSucceeded(string storeId, StripePaymentMethodConfig config, Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not PaymentIntent paymentIntent)
        {
            _logger.LogWarning("PaymentIntent not found in event data");
            return;
        }

        // Find the invoice by PaymentIntent ID
        var invoice = await FindInvoiceByPaymentIntentId(paymentIntent.Id);
        if (invoice == null)
        {
            _logger.LogWarning(
                "No invoice found for PaymentIntent {PaymentIntentId}",
                paymentIntent.Id);
            return;
        }

        // Record the payment
        await RecordPayment(invoice, paymentIntent, config);
    }

    private Task HandlePaymentIntentFailed(string storeId, Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not PaymentIntent paymentIntent)
            return Task.CompletedTask;

        _logger.LogInformation(
            "PaymentIntent {PaymentIntentId} failed: {Error}",
            paymentIntent.Id, paymentIntent.LastPaymentError?.Message);

        // Could emit an event or update invoice logs here
        return Task.CompletedTask;
    }

    private Task HandleChargeRefunded(string storeId, Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Charge charge)
            return Task.CompletedTask;

        _logger.LogWarning(
            "Charge {ChargeId} was refunded for PaymentIntent {PaymentIntentId}",
            charge.Id, charge.PaymentIntentId);

        // TODO: Mark invoice as refunded or emit refund event
        return Task.CompletedTask;
    }

    private Task HandleDisputeCreated(string storeId, Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Dispute dispute)
            return Task.CompletedTask;

        _logger.LogWarning(
            "Dispute created for charge {ChargeId}: {Reason}",
            dispute.ChargeId, dispute.Reason);

        // TODO: Emit dispute event or update invoice
        return Task.CompletedTask;
    }

    private async Task<InvoiceEntity?> FindInvoiceByPaymentIntentId(string paymentIntentId)
    {
        // Search for invoice by tracked destination (PaymentIntent ID)
        var invoice = await _invoiceRepository.GetInvoiceFromAddress(
            StripePlugin.StripePaymentMethodId, paymentIntentId);

        if (invoice?.Id is not {} invoiceId)
            return null;

        return await _invoiceRepository.GetInvoice(invoiceId);
    }

    private async Task RecordPayment(InvoiceEntity invoice, PaymentIntent paymentIntent, StripePaymentMethodConfig config)
    {
        if (!_handlers.TryGetValue(StripePlugin.StripePaymentMethodId, out var handler))
            return;

        var paymentData = new StripePaymentData
        {
            PaymentIntentId = paymentIntent.Id,
            AmountReceived = paymentIntent.AmountReceived,
            Currency = paymentIntent.Currency
        };

        var payment = new PaymentData
        {
            Id = paymentIntent.Id,
            InvoiceDataId = invoice.Id,
            Currency = config.SettlementCurrency,
            Amount = StripeService.FromStripeAmount(paymentIntent.AmountReceived, paymentIntent.Currency),
            Status = PaymentStatus.Settled, // Stripe payments are immediately settled
            Created = DateTimeOffset.UtcNow
        };

        payment.Set(invoice, handler, paymentData);

        if (await _paymentService.AddPayment(payment, [paymentIntent.Id]) is { } addedPayment)
        {
            _logger.LogDebug(
                "Payment recorded with PaymentMethodId={PaymentMethodId}, Id={PaymentId}",
                addedPayment.PaymentMethodId, addedPayment.Id);
            _aggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = addedPayment });
        }

        _logger.LogInformation(
            "Recorded payment for invoice {InvoiceId}: {Amount} {Currency}",
            invoice.Id, payment.Amount, payment.Currency);
    }
}
