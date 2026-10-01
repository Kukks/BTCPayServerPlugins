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

    public LnurlRailCheckoutController(InvoiceRepository invoices, StoreRepository stores, InvoiceActivator activator,
        RailActivationFailures failures)
    {
        _invoices = invoices;
        _stores = stores;
        _activator = activator;
        _failures = failures;
    }

    [HttpPost("activate")]
    [IgnoreAntiforgeryToken]
    [RateLimitsFilter(ZoneLimits.PublicInvoices, Scope = RateLimitsScope.RemoteAddress)]
    public async Task<IActionResult> Activate(string invoiceId, string? rail = null)
    {
        var invoice = await _invoices.GetInvoice(invoiceId);
        if (invoice is null || !CheckoutRails.Applies(invoice)) return NotFound();
        var activateAllOnOpen = rail is not null ||
            (await _stores.GetSettingAsync<LnurlRailSettings>(invoice.StoreId, LnurlRailSettings.Key) ?? new LnurlRailSettings()).ActivateAllRailsOnOpen;
        var now = DateTimeOffset.UtcNow;
        var (activate, skipped) = CheckoutRails.Plan(CheckoutRails.Rails(invoice), rail, activateAllOnOpen,
            p => _failures.Recent(invoiceId, p, now));
        var results = await Task.WhenAll(activate.Select(async p => (Rail: p, Ok: await _activator.ActivateInvoicePaymentMethod(invoiceId, p))));
        foreach (var r in results.Where(r => !r.Ok)) _failures.Record(invoiceId, r.Rail, now);
        return Json(new { failed = skipped.Concat(results.Where(r => !r.Ok).Select(r => r.Rail)).Select(p => p.ToString()) });
    }
}
