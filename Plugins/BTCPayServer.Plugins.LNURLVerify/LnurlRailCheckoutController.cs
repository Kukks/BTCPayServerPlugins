#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NicolasDorier.RateLimits;

namespace BTCPayServer.Plugins.LNURLVerify;

[AllowAnonymous]
[Route("plugins/lnurlverify/checkout/{invoiceId}")]
public class LnurlRailCheckoutController : Controller
{
    private readonly InvoiceRepository _invoices;
    private readonly StoreRepository _stores;
    private readonly InvoiceActivator _activator;
    private readonly RailActivationFailures _failures;
    private readonly RailActivationGate _gate;

    public LnurlRailCheckoutController(InvoiceRepository invoices, StoreRepository stores, InvoiceActivator activator,
        RailActivationFailures failures, RailActivationGate gate)
    {
        _invoices = invoices;
        _stores = stores;
        _activator = activator;
        _failures = failures;
        _gate = gate;
    }

    [HttpPost("activate")]
    [IgnoreAntiforgeryToken]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RouteData, DataKey = "invoiceId")]
    public async Task<IActionResult> Activate([FromRoute] string invoiceId, string? rail = null)
    {
        var invoice = await _invoices.GetInvoice(invoiceId);
        if (invoice is null || !CheckoutRails.Applies(invoice)) return NotFound();
        var activateAllOnOpen = rail is not null ||
            (await _stores.GetSettingAsync<LnurlRailSettings>(invoice.StoreId, LnurlRailSettings.Key) ?? new LnurlRailSettings()).ActivateAllRailsOnOpen;
        var now = DateTimeOffset.UtcNow;
        var (activate, skipped) = CheckoutRails.Plan(CheckoutRails.Rails(invoice), rail, activateAllOnOpen,
            p => _failures.Recent(invoiceId, p, now));
        var results = await Task.WhenAll(activate.Select(async p =>
            (Rail: p, Ok: await _gate.Run(invoiceId, p, () => _activator.ActivateInvoicePaymentMethod(invoiceId, p)))));
        var refused = results.Where(r => !r.Ok).Select(r => r.Rail).ToList();
        if (refused.Count > 0 && await _invoices.GetInvoice(invoiceId) is { } fresh)
        {
            // false also means a concurrent request already activated it
            var inactive = CheckoutRails.Rails(fresh).Where(p => !p.Activated).Select(p => p.PaymentMethodId).ToList();
            refused = refused.Where(inactive.Contains).ToList();
        }
        foreach (var p in refused) _failures.Record(invoiceId, p, now);
        return Json(new { failed = skipped.Concat(refused).Select(p => p.ToString()) });
    }
}
