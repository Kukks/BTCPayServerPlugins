#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>An LNURL-supplied payment method whose destinations settle through LUD-21 verify.</summary>
public interface ILnurlRailHandler : IPaymentMethodHandler
{
    string Label { get; }
    IEnumerable<TrackedDestination> Tracked(InvoiceEntity invoice, PaymentPrompt prompt);
    bool NeedsReissue(PaymentPrompt prompt);
}

/// <summary>Records settled rail destinations as BTCPay payments and keeps every invoice's destinations current.</summary>
public class LnurlRailRecorder : EventHostedServiceBase
{
    private sealed class Rebuild { }
    private sealed record RailSettled(TrackedDestination Destination, string Reference);
    private sealed record RailForgotten(TrackedDestination Destination, string Reason);

    private static readonly TimeSpan RebuildRetryDelay = TimeSpan.FromMinutes(1);

    private readonly InvoiceRepository _invoices;
    private readonly PaymentService _payments;
    private readonly InvoiceActivator _activator;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly TokenActivations _tokenActivations;
    private readonly ILogger _logger;
    private readonly Action<TrackedDestination, string> _onSettled;
    private readonly Action<TrackedDestination, string> _onForgotten;

    public LnurlRailRecorder(EventAggregator eventAggregator, ILogger<LnurlRailRecorder> logger, InvoiceRepository invoices,
        PaymentService payments, InvoiceActivator activator, PaymentMethodHandlerDictionary handlers, TokenActivations tokenActivations)
        : base(eventAggregator, logger)
    {
        _invoices = invoices;
        _payments = payments;
        _activator = activator;
        _handlers = handlers;
        _tokenActivations = tokenActivations;
        _logger = logger;
        _onSettled = (d, reference) => PushEvent(new RailSettled(d, reference));
        _onForgotten = (d, reason) => PushEvent(new RailForgotten(d, reason));
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<InvoiceDataChangedEvent>();
        TrackedDestinationRegistry.Settled += _onSettled;
        TrackedDestinationRegistry.Forgotten += _onForgotten;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        PushEvent(new Rebuild());
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        TrackedDestinationRegistry.Settled -= _onSettled;
        TrackedDestinationRegistry.Forgotten -= _onForgotten;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case Rebuild:
                await RebuildTracking(cancellationToken);
                break;
            case RailSettled s:
                await Record(s.Destination, s.Reference);
                break;
            case RailForgotten f:
                var logs = new InvoiceLogs();
                logs.Write($"{f.Destination.PaymentMethodId}: the LNURL service no longer knows {f.Destination.Destination} ({f.Reason}); " +
                           "a payment to it cannot be detected", InvoiceEventData.EventSeverity.Warning);
                await _invoices.AddInvoiceLogs(f.Destination.InvoiceId, logs);
                break;
            case InvoiceEvent { Name: InvoiceEvent.ReceivedPayment } e when IsPartial(e.Invoice.GetInvoiceState()):
                await Reissue(e.Invoice);
                break;
            case InvoiceDataChangedEvent c when IsPartial(c.State):
                if (await _invoices.GetInvoice(c.InvoiceId) is { } changed) await Reissue(changed);
                break;
        }
    }

    private async Task RebuildTracking(CancellationToken cancellationToken)
    {
        var queryFailed = false;
        foreach (var handler in _handlers.OfType<ILnurlRailHandler>())
        {
            InvoiceEntity[] monitored;
            try { monitored = await _invoices.GetMonitoredInvoices(handler.PaymentMethodId, cancellationToken); }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(e, "Could not list the invoices monitored for {PaymentMethodId}; retrying the rebuild in {Delay}",
                    handler.PaymentMethodId, RebuildRetryDelay);
                queryFailed = true;
                continue;
            }
            foreach (var invoice in monitored)
            {
                try { Track(invoice, handler); }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Could not track the {PaymentMethodId} destinations of invoice {InvoiceId}", handler.PaymentMethodId, invoice.Id);
                }
            }
        }
        // Nothing re-tracks these destinations until a restart, so a failed rebuild retries itself.
        if (queryFailed) _ = RetryRebuildLater();
    }

    private async Task RetryRebuildLater()
    {
        try
        {
            await Task.Delay(RebuildRetryDelay, CancellationToken);
            PushEvent(new Rebuild());
        }
        catch (OperationCanceledException) { }
    }

    private static void Track(InvoiceEntity invoice, ILnurlRailHandler handler)
    {
        if (invoice.GetPaymentPrompt(handler.PaymentMethodId) is not { Activated: true, Details: not null } prompt) return;
        foreach (var d in handler.Tracked(invoice, prompt))
            TrackedDestinationRegistry.Add(d);
    }

    private async Task Record(TrackedDestination d, string reference)
    {
        try
        {
            var pmi = PaymentMethodId.Parse(d.PaymentMethodId);
            var invoice = await _invoices.GetInvoice(d.InvoiceId);
            if (invoice is null || !_handlers.TryGetValue(pmi, out var handler)) return;
            var id = PaymentId(reference, d.VerifyUrl);
            if (invoice.GetPayments(false).Any(p => p.Id == id && p.PaymentMethodId == pmi))
            {
                // An earlier attempt may have stored the payment and failed before telling the watcher.
                EventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
                return;
            }
            var data = Payment(invoice, handler, d, reference);
            if (await _payments.AddPayment(data) is not { } payment)
            {
                // null is a duplicate or a failed insert, and Apply already untracked the destination; retrying is idempotent.
                _logger.LogWarning("The LNURL rail payment {Reference} for invoice {InvoiceId} was not stored; retrying on the next poll",
                    reference, d.InvoiceId);
                TrackedDestinationRegistry.Add(d);
                return;
            }
            // Core's listeners publish the post-payment invoice; LightningListener sizes a new BOLT11 from its due.
            EventAggregator.Publish(new InvoiceEvent(await _invoices.GetInvoice(invoice.Id) ?? invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
            EventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not record the LNURL rail payment {Reference} for invoice {InvoiceId}; retrying on the next poll",
                reference, d.InvoiceId);
            TrackedDestinationRegistry.Add(d);
        }
    }

    private async Task Reissue(InvoiceEntity invoice)
    {
        foreach (var handler in _handlers.OfType<ILnurlRailHandler>())
        {
            if (invoice.GetPaymentPrompt(handler.PaymentMethodId) is not { Activated: true, Details: not null } prompt ||
                !handler.NeedsReissue(prompt)) continue;
            if (handler is LnurlTokenPaymentHandler)
                await _tokenActivations.Run(invoice.Id, handler.PaymentMethodId, Array.Empty<string>(),
                    () => _activator.ActivateInvoicePaymentMethod(invoice.Id, handler.PaymentMethodId, forceNew: true));
            else
                await _activator.ActivateInvoicePaymentMethod(invoice.Id, handler.PaymentMethodId, forceNew: true);
        }
    }

    private static bool IsPartial(InvoiceState state) =>
        state.Status == InvoiceStatus.New && state.ExceptionStatus == InvoiceExceptionStatus.PaidPartial;

    internal static PaymentData Payment(InvoiceEntity invoice, IPaymentMethodHandler handler, TrackedDestination d, string reference)
    {
        var data = new PaymentData
        {
            Id = PaymentId(reference, d.VerifyUrl), Created = DateTimeOffset.UtcNow, Status = PaymentStatus.Settled, Currency = "BTC",
            Amount = LnurlRailPaymentHandler.AmountBtc(d.AmountMsat)
        }.Set(invoice, handler, new LnurlRailPaymentData { PaymentReference = reference, VerifyUrl = d.VerifyUrl, Destination = d.Destination, Asset = d.Asset });
        // Set stamps the prompt's destination, which on a token prompt is the last network requested; Greenfield,
        // the payment webhooks and the receipt all report this blob.
        var blob = data.GetBlob();
        blob.Destination = d.Destination;
        data.SetBlob(blob);
        return data;
    }

    /// <summary>Payments are keyed (Id, PaymentMethodId) across every invoice, and one Ark transaction can pay two destinations.</summary>
    public static string PaymentId(string reference, string verifyUrl) =>
        $"{reference}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifyUrl)))[..16].ToLowerInvariant()}";

    /// <summary>Both partial-payment signals can fire for one payment; a destination already agreed for the due needs nothing.</summary>
    public static bool NeedsReissue(decimal dueBtc, long agreedMsat) =>
        dueBtc > 0m && LnurlRailPaymentHandler.MsatOf(dueBtc) != agreedMsat;
}
