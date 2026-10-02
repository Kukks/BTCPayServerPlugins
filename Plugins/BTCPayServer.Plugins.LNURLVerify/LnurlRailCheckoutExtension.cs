#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Turns the Lightning or on-chain tab of an invoice holding LNURL rails into one "Bitcoin" tab.</summary>
public class LnurlRailCheckoutExtension : IGlobalCheckoutModelExtension
{
    public const string ComponentName = "lnurlRailsCheckout";
    private const string TabName = "Bitcoin";
    private static readonly PaymentMethodId CoreLnurl = PaymentTypes.LNURL.GetPaymentMethodId("BTC");

    private readonly IPaymentLinkExtension? _chainLink;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<LnurlRailCheckoutExtension> _logger;

    public LnurlRailCheckoutExtension(IEnumerable<IPaymentLinkExtension> links, PaymentMethodHandlerDictionary handlers,
        ILogger<LnurlRailCheckoutExtension> logger)
    {
        _chainLink = links.FirstOrDefault(l => l.PaymentMethodId == LnurlRailProvisioning.OnChain);
        _handlers = handlers;
        _logger = logger;
    }

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        // Core calls global extensions unguarded: a throw here would take down the checkout page.
        try { Reskin(context); }
        catch (Exception e) { _logger.LogWarning(e, "LNURL rails checkout skipped for invoice {InvoiceId}", context.InvoiceEntity.Id); }
    }

    private void Reskin(CheckoutModelContext context)
    {
        if (!CheckoutRails.Applies(context.InvoiceEntity)) return;
        var model = context.Model;
        foreach (var pm in model.AvailablePaymentMethods.Where(pm => LnurlRails.IsRail(pm.PaymentMethodId)))
            pm.Displayed = false;
        var selected = PaymentMethodId.Parse(model.PaymentMethodId);
        // Core resolves to its own LNURL-pay method when BTC-LN fails; that tab must still reach the rails.
        if (!IsBitcoinRail(selected) && selected != CoreLnurl) return;

        var prompts = CheckoutRails.Rails(context.InvoiceEntity);
        var rails = prompts.Select(p => State(p, context.UrlHelper)).OfType<RailState>().ToList();
        var lightningDue = prompts.FirstOrDefault(p => p.Activated && p.PaymentMethodId == LnurlRailProvisioning.Lightning)?.Calculate().Due;
        var merged = Bip321.Merge(rails, lightningDue);
        var json = new JArray(rails.Select(r => new JObject
        {
            ["pmi"] = r.PaymentMethodId.ToString(), ["label"] = r.Label, ["active"] = r.Active,
            ["destination"] = JsonString(r.Destination), ["uri"] = JsonString(r.Uri), ["qr"] = JsonString(r.Uri is null ? null : Bip321.Qr(r.Uri))
        }));

        foreach (var pm in model.AvailablePaymentMethods)
        {
            if (pm.PaymentMethodId == selected) { pm.Displayed = true; pm.PaymentMethodName = TabName; }
            else if (IsBitcoinRail(pm.PaymentMethodId) || pm.PaymentMethodId == CoreLnurl) pm.Displayed = false;
        }
        model.PaymentMethodName = TabName;
        model.CheckoutBodyComponentName = ComponentName;
        model.AdditionalData["lnurlRails"] = json;
        // NFC needs this on a BTC-CHAIN tab; readers assume the URI carries lightning, so only while that rail is live.
        model.OnChainWithLnInvoiceFallback |= rails.Any(r => r.PaymentMethodId == LnurlRailProvisioning.Lightning && r.Active);
        if (merged is not null)
        {
            model.InvoiceBitcoinUrl = merged;
            model.InvoiceBitcoinUrlQR = Bip321.Qr(merged);
        }
    }

    private static bool IsBitcoinRail(PaymentMethodId pmi) =>
        pmi == LnurlRailProvisioning.Lightning || pmi == LnurlRailProvisioning.OnChain || LnurlRails.IsRail(pmi);

    // The implicit string -> JToken conversion types a null as String; keep it a JSON null token.
    private static JToken JsonString(string? s) => s is null ? JValue.CreateNull() : new JValue(s);

    private RailState? State(PaymentPrompt p, IUrlHelper url)
    {
        var pmi = p.PaymentMethodId;
        if (pmi == LnurlRailProvisioning.Lightning)
            return new RailState(pmi, "Lightning", p.Activated, p.Destination, p.Activated ? "lightning:" + p.Destination : null, "lightning");
        if (pmi == LnurlRailProvisioning.OnChain)
            return new RailState(pmi, "On-chain", p.Activated, p.Destination, p.Activated ? _chainLink?.GetPaymentLink(p, url) : null, null);
        var rail = LnurlRails.For(pmi)!;
        string? uri = null;
        if (p is { Activated: true, Details: { } details } && _handlers.TryGet(pmi) is LnurlRailPaymentHandler handler)
        {
            var agreedMsat = handler.ParsePaymentPromptDetails(details).AmountMsat;
            // Stale after a partial payment: the destination still asks the old amount until its re-issue lands.
            if (LnurlRailRecorder.NeedsReissue(p.Calculate().Due, agreedMsat)) return null;
            var amount = LnurlRailPaymentHandler.AmountBtc(agreedMsat);
            uri = rail.UriParam is null
                ? Bip321.Build(p.Destination, amount, Array.Empty<KeyValuePair<string, string>>())
                : Bip321.Build(null, amount, new[] { new KeyValuePair<string, string>(rail.UriParam, p.Destination) });
        }
        return new RailState(pmi, rail.Label, p.Activated, p.Destination, uri, rail.UriParam);
    }
}
