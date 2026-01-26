#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using BTCPayServer.Plugins.Stripe.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stripe.Controllers;

/// <summary>
/// API controller for checkout UI interactions.
/// Provides synchronous payment verification for the Stripe Payment Element.
/// </summary>
[Route("api/plugins/stripe")]
[ApiController]
[EnableCors(CorsPolicies.All)]
public class StripeApiController : ControllerBase
{
    private readonly StoreRepository _storeRepository;
    private readonly StripeService _stripeService;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<StripeApiController> _logger;
    private readonly EventAggregator _aggregator;

    public StripeApiController(
        StoreRepository storeRepository,
        StripeService stripeService,
        InvoiceRepository invoiceRepository,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        ILogger<StripeApiController> logger,
        EventAggregator aggregator)
    {
        _storeRepository = storeRepository;
        _stripeService = stripeService;
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

    /// <summary>
    /// Verify a Stripe payment after the customer completes it in the frontend.
    /// Called by the checkout UI after confirmPayment succeeds.
    /// </summary>
    [HttpPost("verify/{invoiceId}")]
    public async Task<IActionResult> VerifyPayment(string invoiceId, [FromBody] VerifyPaymentRequest request)
    {
        if (string.IsNullOrEmpty(request?.PaymentIntentId))
        {
            return BadRequest(new { error = "PaymentIntentId is required" });
        }

        var invoice = await _invoiceRepository.GetInvoice(invoiceId);
        if (invoice == null)
        {
            return NotFound(new { error = "Invoice not found" });
        }

        var store = await _storeRepository.FindStore(invoice.StoreId);
        if (store == null)
        {
            return BadRequest(new { error = "Store not found" });
        }

        var config = GetConfig(store);
        if (config is not { IsConfigured: true })
        {
            return BadRequest(new { error = "Stripe not configured for this store" });
        }

        // Verify the PaymentIntent matches what's stored in the invoice
        var prompt = invoice.GetPaymentPrompt(StripePlugin.StripePaymentMethodId);
        if (prompt == null)
        {
            return BadRequest(new { error = "Stripe payment method not found for this invoice" });
        }

        if (!_handlers.TryGetValue(StripePlugin.StripePaymentMethodId, out var handler))
        {
            return BadRequest(new { error = "Stripe handler not registered" });
        }

        var promptDetails = (StripePromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
        if (promptDetails.PaymentIntentId != request.PaymentIntentId)
        {
            _logger.LogWarning(
                "PaymentIntent mismatch for invoice {InvoiceId}: expected {Expected}, got {Actual}",
                invoiceId, promptDetails.PaymentIntentId, request.PaymentIntentId);
            return BadRequest(new { error = "PaymentIntent does not match invoice" });
        }

        try
        {
            // Fetch the PaymentIntent from Stripe to verify status
            var paymentIntent = await _stripeService.GetPaymentIntent(config, request.PaymentIntentId);

            if (paymentIntent.Status != "succeeded")
            {
                _logger.LogInformation(
                    "PaymentIntent {PaymentIntentId} status is {Status}, not succeeded",
                    paymentIntent.Id, paymentIntent.Status);

                return Ok(new VerifyPaymentResponse
                {
                    Success = false,
                    Status = paymentIntent.Status,
                    RequiresAction = paymentIntent.Status == "requires_action",
                    ClientSecret = paymentIntent.Status == "requires_action" ? paymentIntent.ClientSecret : null
                });
            }

            // Record the payment
            var charge = paymentIntent.LatestCharge;

            var paymentData = new StripePaymentData
            {
                PaymentIntentId = paymentIntent.Id,
                ChargeId = charge?.Id,
                AmountReceived = paymentIntent.AmountReceived,
                Currency = paymentIntent.Currency,
                PaymentMethodType = charge?.PaymentMethodDetails?.Type,
                Last4 = charge?.PaymentMethodDetails?.Card?.Last4,
                Brand = charge?.PaymentMethodDetails?.Card?.Brand
            };

            var payment = new PaymentData
            {
                Id = paymentIntent.Id,
                InvoiceDataId = invoice.Id,
                Currency = config.SettlementCurrency,
                Amount = StripeService.FromStripeAmount(paymentIntent.AmountReceived, paymentIntent.Currency),
                Status = PaymentStatus.Settled,
                Created = DateTimeOffset.UtcNow
            };

            payment.Set(invoice, handler,paymentData);

            if (await _paymentService.AddPayment(payment, [paymentIntent.Id]) is {} addedPayment)
            {
                _aggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = addedPayment });
            }

            _logger.LogInformation(
                "Payment verified and recorded for invoice {InvoiceId}: {Amount} {Currency}",
                invoiceId, payment.Amount, payment.Currency);

            return Ok(new VerifyPaymentResponse
            {
                Success = true,
                Status = "succeeded",
                PaymentId = paymentIntent.Id,
                AmountPaid = StripeService.FromStripeAmount(paymentIntent.AmountReceived, paymentIntent.Currency),
                Currency = paymentIntent.Currency
            });
        }
        catch (global::Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Stripe API error while verifying payment for invoice {InvoiceId}", invoiceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get the current status of a PaymentIntent for an invoice.
    /// Used by the checkout UI for polling.
    /// </summary>
    [HttpGet("status/{invoiceId}")]
    public async Task<IActionResult> GetPaymentStatus(string invoiceId)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId);
        if (invoice == null)
        {
            return NotFound(new { error = "Invoice not found" });
        }

        var store = await _storeRepository.FindStore(invoice.StoreId);
        if (store == null)
        {
            return BadRequest(new { error = "Store not found" });
        }

        var config = GetConfig(store);
        if (config is not { IsConfigured: true })
        {
            return BadRequest(new { error = "Stripe not configured for this store" });
        }

        var prompt = invoice.GetPaymentPrompt(StripePlugin.StripePaymentMethodId);
        if (prompt == null)
        {
            return BadRequest(new { error = "Stripe payment method not found for this invoice" });
        }

        if (!_handlers.TryGetValue(StripePlugin.StripePaymentMethodId, out var handler))
        {
            return BadRequest(new { error = "Stripe handler not registered" });
        }

        var promptDetails = (StripePromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);

        try
        {
            var paymentIntent = await _stripeService.GetPaymentIntent(config, promptDetails.PaymentIntentId);

            return Ok(new PaymentStatusResponse
            {
                PaymentIntentId = paymentIntent.Id,
                Status = paymentIntent.Status,
                AmountReceived = paymentIntent.AmountReceived,
                Currency = paymentIntent.Currency,
                RequiresAction = paymentIntent.Status == "requires_action"
            });
        }
        catch (global::Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Error getting payment status for invoice {InvoiceId}", invoiceId);
            return BadRequest(new { error = ex.Message });
        }
    }
}

public class VerifyPaymentRequest
{
    public string? PaymentIntentId { get; set; }
}

public class VerifyPaymentResponse
{
    public bool Success { get; set; }
    public string? Status { get; set; }
    public bool RequiresAction { get; set; }
    public string? ClientSecret { get; set; }
    public string? PaymentId { get; set; }
    public decimal? AmountPaid { get; set; }
    public string? Currency { get; set; }
}

public class PaymentStatusResponse
{
    public string? PaymentIntentId { get; set; }
    public string? Status { get; set; }
    public long AmountReceived { get; set; }
    public string? Currency { get; set; }
    public bool RequiresAction { get; set; }
}
