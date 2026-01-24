#nullable enable
using System;
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
/// - Cancels PaymentIntents when invoices expire, are marked invalid, or complete
/// - Handles partial payment scenarios by cancelling existing PaymentIntents
/// </summary>
public class StripeInvoiceListener : EventHostedServiceBase
{
    private readonly StripeService _stripeService;
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<StripeInvoiceListener> _logger;

    public StripeInvoiceListener(
        EventAggregator eventAggregator,
        StripeService stripeService,
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        ILogger<StripeInvoiceListener> logger)
        : base(eventAggregator, logger)
    {
        _stripeService = stripeService;
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _logger = logger;
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
    /// If Stripe has an active PaymentIntent, it should be cancelled so a new one
    /// with the correct remaining amount can be created on next checkout view.
    /// </summary>
    private async Task HandlePartialPayment(InvoiceEvent invoiceEvent, CancellationToken cancellationToken)
    {
        var invoice = invoiceEvent.Invoice;
        var payment = invoiceEvent.Payment;

        // Only process if payment was NOT via Stripe (partial payment via another method)
        if (payment?.PaymentMethodId == StripePlugin.StripePaymentMethodId)
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
            "Partial payment received via {PaymentMethod}. Cancelling Stripe PaymentIntent {PaymentIntentId} for invoice {InvoiceId}",
            payment?.PaymentMethodId?.ToString() ?? "unknown", paymentIntentId, invoice.Id);

        // Cancel the existing PaymentIntent - a new one will be created with
        // the updated amount when the checkout is viewed again
        await _stripeService.CancelPaymentIntent(config, paymentIntentId, cancellationToken);
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

        _logger.LogInformation(
            "Invoice {InvoiceId} paid via {PaymentMethod}. Cancelling unused Stripe PaymentIntent {PaymentIntentId}",
            invoice.Id, payment?.PaymentMethodId?.ToString() ?? "unknown", paymentIntentId);

        await _stripeService.CancelPaymentIntent(config, paymentIntentId, cancellationToken);
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
