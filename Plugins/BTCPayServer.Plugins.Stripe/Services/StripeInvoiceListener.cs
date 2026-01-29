#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Stripe.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Stripe.Services;

/// <summary>
/// Listens for BTCPay invoice events and manages Stripe PaymentIntents accordingly.
/// - Checks pending PaymentIntents on startup to catch missed payments
/// - Cancels PaymentIntents when invoices expire, are marked invalid, or complete
/// - Handles partial payment scenarios by creating new PaymentIntents with remaining amount
/// </summary>
public class StripeInvoiceListener : EventHostedServiceBase
{
    private readonly StripeService _stripeService;
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<StripeInvoiceListener> _logger;

    public StripeInvoiceListener(
        EventAggregator eventAggregator,
        StripeService stripeService,
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        ILogger<StripeInvoiceListener> logger)
        : base(eventAggregator, logger)
    {
        _stripeService = stripeService;
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _handlers = handlers;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Stripe invoice listener, checking for pending payments...");

        try
        {
            await CheckPendingPaymentIntents(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking pending Stripe PaymentIntents on startup");
        }

        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// On startup, check all pending invoices with Stripe PaymentIntents to see if any
    /// payments were completed while BTCPay was offline (missed webhooks).
    /// </summary>
    private async Task CheckPendingPaymentIntents(CancellationToken cancellationToken)
    {
        // Get all "New" invoices (waiting for payment)
        var pendingInvoices = await _invoiceRepository.GetMonitoredInvoices(StripePlugin.StripePaymentMethodId, cancellationToken);

        if (pendingInvoices?.Length is null or 0)
        {
            _logger.LogInformation("No pending invoices found for Stripe payment method");
            return;
        }

        _logger.LogInformation(
            "Found {Count} pending invoices with Stripe PaymentIntents, checking status...",
            pendingInvoices.Length);

        foreach (var invoice in pendingInvoices)
        {
            try
            {
                await CheckAndRecordPaymentIfSucceeded(invoice, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Error checking PaymentIntent for invoice {InvoiceId}",
                    invoice.Id);
            }
        }
    }

    /// <summary>
    /// Check if a PaymentIntent has succeeded and record the payment if so.
    /// </summary>
    private async Task CheckAndRecordPaymentIfSucceeded(InvoiceEntity invoice, CancellationToken cancellationToken)
    {
        var paymentIntentId = GetPaymentIntentIdFromInvoice(invoice);
        if (string.IsNullOrEmpty(paymentIntentId))
            return;

        var store = await _storeRepository.FindStore(invoice.StoreId);
        if (store == null)
            return;

        var config = GetConfig(store);
        if (config is not { IsConfigured: true })
            return;

        // Fetch the PaymentIntent from Stripe
        var paymentIntent = await _stripeService.GetPaymentIntent(config, paymentIntentId, cancellationToken);

        if (paymentIntent.Status != "succeeded")
        {
            _logger.LogDebug(
                "PaymentIntent {PaymentIntentId} for invoice {InvoiceId} status is {Status}",
                paymentIntentId, invoice.Id, paymentIntent.Status);
            return;
        }

        // Check if payment was already recorded
        var existingPayments = invoice.GetPayments(true);
        if (existingPayments.Any(p =>
            p.PaymentMethodId == StripePlugin.StripePaymentMethodId &&
            p.Id == paymentIntentId))
        {
            _logger.LogDebug(
                "Payment for PaymentIntent {PaymentIntentId} already recorded for invoice {InvoiceId}",
                paymentIntentId, invoice.Id);
            return;
        }

        _logger.LogInformation(
            "Found succeeded PaymentIntent {PaymentIntentId} for invoice {InvoiceId} - recording payment",
            paymentIntentId, invoice.Id);

        // Record the payment
        await RecordStripePayment(invoice, paymentIntent, config);
    }

    /// <summary>
    /// Record a Stripe payment for an invoice.
    /// </summary>
    private async Task RecordStripePayment(
        InvoiceEntity invoice,
        global::Stripe.PaymentIntent paymentIntent,
        StripePaymentMethodConfig config)
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
            Status = PaymentStatus.Settled,
            Created = DateTimeOffset.UtcNow
        };

        payment.Set(invoice, handler, paymentData);

        await _paymentService.AddPayment(payment, [paymentIntent.Id]);

        _logger.LogInformation(
            "Recorded missed Stripe payment for invoice {InvoiceId}: {Amount} {Currency} (PaymentIntent {PaymentIntentId})",
            invoice.Id, payment.Amount, payment.Currency, paymentIntent.Id);
    }

    private StripePaymentMethodConfig? GetConfig(StoreData store)
    {
        return store.GetPaymentMethodConfig<StripePaymentMethodConfig>(
            StripePlugin.StripePaymentMethodId, _handlers);
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not InvoiceEvent invoiceEvent)
        {
            await base.ProcessEvent(evt, cancellationToken);
            return;
        }

        try
        {
            switch (invoiceEvent.EventCode)
            {
                // Invoice is no longer active - cancel any pending PaymentIntent
                case InvoiceEventCode.Expired:
                case InvoiceEventCode.MarkedInvalid:
                case InvoiceEventCode.Completed:
                case InvoiceEventCode.MarkedCompleted:
                    await HandleInvoiceInactive(invoiceEvent, cancellationToken);
                    break;

                // Payment received via another method - may need to adjust Stripe amount
                case InvoiceEventCode.ReceivedPayment:
                    await HandlePartialPayment(invoiceEvent, cancellationToken);
                    break;

                // Invoice fully paid - cancel any unused PaymentIntents
                case InvoiceEventCode.PaidInFull:
                    await HandleInvoicePaid(invoiceEvent, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing invoice event {EventCode} for invoice {InvoiceId}",
                invoiceEvent.EventCode, invoiceEvent.InvoiceId);
        }

        await base.ProcessEvent(evt, cancellationToken);
    }

    /// <summary>
    /// Handle when an invoice becomes inactive (expired, invalid, completed).
    /// Cancel any pending Stripe PaymentIntent.
    /// </summary>
    private async Task HandleInvoiceInactive(InvoiceEvent invoiceEvent, CancellationToken cancellationToken)
    {
        var invoice = invoiceEvent.Invoice;
        var paymentIntentId = GetPaymentIntentIdFromInvoice(invoice);

        if (string.IsNullOrEmpty(paymentIntentId))
            return;

        var store = await _storeRepository.FindStore(invoice.StoreId);
        if (store == null)
            return;

        var config = GetConfig(store);
        if (config == null || !config.IsConfigured)
            return;

        _logger.LogInformation(
            "Cancelling PaymentIntent {PaymentIntentId} for inactive invoice {InvoiceId} (Event: {EventCode})",
            paymentIntentId, invoice.Id, invoiceEvent.EventCode);

        await _stripeService.CancelPaymentIntent(config, paymentIntentId, cancellationToken);
    }

    /// <summary>
    /// Handle when a partial payment is received via another payment method.
    /// Cancels the existing PaymentIntent and creates a new one with the remaining amount.
    /// </summary>
    private async Task HandlePartialPayment(InvoiceEvent invoiceEvent, CancellationToken cancellationToken)
    {
        var invoice = invoiceEvent.Invoice;
        var payment = invoiceEvent.Payment;

        // DEBUG: Log payment method check
        _logger.LogInformation(
            "HandlePartialPayment for invoice {InvoiceId}: Payment={PaymentExists}, PaymentMethodId={PaymentMethodId}, IsStripe={IsStripe}",
            invoice.Id,
            payment != null,
            payment?.PaymentMethodId?.ToString() ?? "null",
            payment?.PaymentMethodId == StripePlugin.StripePaymentMethodId);

        // Only process if payment was NOT via Stripe (partial payment via another method)
        if (payment?.PaymentMethodId == StripePlugin.StripePaymentMethodId)
            return;

        var prompt = invoice.GetPaymentPrompt(StripePlugin.StripePaymentMethodId);
        if (prompt?.Details == null)
            return;

        var paymentIntentId = GetPaymentIntentIdFromInvoice(invoice);
        if (string.IsNullOrEmpty(paymentIntentId))
            return;

        var store = await _storeRepository.FindStore(invoice.StoreId);
        if (store == null)
            return;

        var config = GetConfig(store);
        if (config == null || !config.IsConfigured)
            return;

        _logger.LogInformation(
            "Partial payment received via {PaymentMethod}. Checking Stripe PaymentIntent for invoice {InvoiceId}",
            payment?.PaymentMethodId?.ToString() ?? "unknown", invoice.Id);

        // Check if PaymentIntent has already succeeded (payment was made but not recorded yet)
        try
        {
            var existingPaymentIntent = await _stripeService.GetPaymentIntent(config, paymentIntentId, cancellationToken);
            if (existingPaymentIntent.Status == "succeeded")
            {
                _logger.LogInformation(
                    "PaymentIntent {PaymentIntentId} already succeeded - recording payment instead of cancelling",
                    paymentIntentId);

                // Record the payment that was made
                await RecordStripePayment(invoice, existingPaymentIntent, config);
                return;
            }

            // PaymentIntent not yet succeeded, safe to cancel and create new one
            await _stripeService.CancelPaymentIntent(config, paymentIntentId, cancellationToken);
        }
        catch (global::Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            _logger.LogWarning("PaymentIntent {PaymentIntentId} not found - may have already been cancelled", paymentIntentId);
            // Continue to create new PaymentIntent
        }

        // Calculate the remaining amount due
        var accounting = prompt.Calculate();
        var remainingDue = accounting.Due;

        if (remainingDue <= 0)
        {
            _logger.LogInformation(
                "No remaining balance for Stripe payment on invoice {InvoiceId}",
                invoice.Id);
            return;
        }

        // Create a new PaymentIntent with the remaining amount
        var newPaymentIntent = await _stripeService.CreatePaymentIntent(
            config,
            remainingDue,
            invoice.Id,
            cancellationToken: cancellationToken);

        // Update the prompt details with the new PaymentIntent
        var newPromptDetails = new StripePromptDetails
        {
            ClientSecret = newPaymentIntent.ClientSecret,
            PublishableKey = config.PublishableKey!,
            PaymentIntentId = newPaymentIntent.Id,
            Amount = remainingDue,
            Currency = config.SettlementCurrency
        };

        prompt.Destination = newPaymentIntent.Id;
        prompt.Details = Newtonsoft.Json.Linq.JObject.FromObject(newPromptDetails);

        await _invoiceRepository.UpdatePrompt(invoice.Id, prompt);

        _logger.LogInformation(
            "Created new PaymentIntent {NewPaymentIntentId} for remaining amount {Amount} {Currency} on invoice {InvoiceId} (cancelled {OldPaymentIntentId})",
            newPaymentIntent.Id, remainingDue, config.SettlementCurrency, invoice.Id, paymentIntentId);
    }

    /// <summary>
    /// Handle when an invoice is paid in full.
    /// If paid via Stripe, record the payment. Otherwise, cancel any pending PaymentIntent.
    /// </summary>
    private async Task HandleInvoicePaid(InvoiceEvent invoiceEvent, CancellationToken cancellationToken)
    {
        var invoice = invoiceEvent.Invoice;
        var payment = invoiceEvent.Payment;

        // If paid via Stripe, no action needed (payment already recorded)
        if (payment?.PaymentMethodId == StripePlugin.StripePaymentMethodId)
            return;

        // Paid via another method - cancel any pending Stripe PaymentIntent
        var paymentIntentId = GetPaymentIntentIdFromInvoice(invoice);
        if (string.IsNullOrEmpty(paymentIntentId))
            return;

        var store = await _storeRepository.FindStore(invoice.StoreId);
        if (store == null)
            return;

        var config = GetConfig(store);
        if (config == null || !config.IsConfigured)
            return;

        try
        {
            // Check if PaymentIntent succeeded but wasn't recorded
            var existingPaymentIntent = await _stripeService.GetPaymentIntent(config, paymentIntentId, cancellationToken);
            if (existingPaymentIntent.Status == "succeeded")
            {
                // Check if payment already recorded
                var existingPayments = invoice.GetPayments(true);
                var alreadyRecorded = existingPayments.Any(p =>
                    p.PaymentMethodId == StripePlugin.StripePaymentMethodId &&
                    p.Id == paymentIntentId);

                if (!alreadyRecorded)
                {
                    _logger.LogInformation(
                        "Invoice {InvoiceId} marked paid but Stripe PaymentIntent {PaymentIntentId} succeeded - recording missed payment",
                        invoice.Id, paymentIntentId);
                    await RecordStripePayment(invoice, existingPaymentIntent, config);
                    return;
                }
            }

            // PaymentIntent not succeeded or already recorded - safe to cancel
            _logger.LogInformation(
                "Invoice {InvoiceId} paid via {PaymentMethod}. Cancelling unused Stripe PaymentIntent {PaymentIntentId}",
                invoice.Id, payment?.PaymentMethodId?.ToString() ?? "unknown", paymentIntentId);

            await _stripeService.CancelPaymentIntent(config, paymentIntentId, cancellationToken);
        }
        catch (global::Stripe.StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            _logger.LogDebug("PaymentIntent {PaymentIntentId} not found - already cancelled or processed", paymentIntentId);
        }
    }

    /// <summary>
    /// Extract the PaymentIntent ID from an invoice's Stripe payment prompt.
    /// </summary>
    private string? GetPaymentIntentIdFromInvoice(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(StripePlugin.StripePaymentMethodId);
        if (prompt?.Details == null)
            return null;

        try
        {
            if (!_handlers.TryGetValue(StripePlugin.StripePaymentMethodId, out var handler))
                return null;

            var details = (StripePromptDetails)handler.ParsePaymentPromptDetails(prompt.Details);
            return details.PaymentIntentId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse Stripe prompt details for invoice {InvoiceId}",
                invoice.Id);
            return null;
        }
    }
}
