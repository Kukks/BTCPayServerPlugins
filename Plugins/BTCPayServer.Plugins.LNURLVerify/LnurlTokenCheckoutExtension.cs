#nullable enable
using System;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Gives an asset's tab its component: a chip per network, and the selected network's QR, amount and wallet link.</summary>
public class LnurlTokenCheckoutExtension : ICheckoutModelExtension
{
    public const string ComponentName = "lnurlTokenCheckout";

    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<LnurlTokenCheckoutExtension> _logger;

    public LnurlTokenCheckoutExtension(PaymentMethodId paymentMethodId, PaymentMethodHandlerDictionary handlers,
        ILogger<LnurlTokenCheckoutExtension> logger)
    {
        PaymentMethodId = paymentMethodId;
        _handlers = handlers;
        _logger = logger;
    }

    public PaymentMethodId PaymentMethodId { get; }
    public string Image => "";
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        context.Model.CheckoutBodyComponentName = ComponentName;
        // Core calls checkout extensions unguarded: a throw here would take down the checkout page.
        try
        {
            if (_handlers.TryGet(PaymentMethodId) is LnurlTokenPaymentHandler handler)
                context.Model.AdditionalData["lnurlToken"] = Model(handler, context.Prompt, DateTimeOffset.UtcNow);
        }
        catch (Exception e) { _logger.LogWarning(e, "LNURL token checkout skipped for invoice {InvoiceId}", context.InvoiceEntity.Id); }
    }

    public static JObject Model(LnurlTokenPaymentHandler handler, PaymentPrompt prompt, DateTimeOffset now)
    {
        var details = prompt is { Activated: true, Details: not null }
            ? handler.ParsePaymentPromptDetails(prompt.Details)
            : new LnurlTokenPromptDetails { Unit = handler.Code };
        var dueMsat = LnurlRailPaymentHandler.MsatOf(prompt.Calculate().Due);
        var networks = new JArray();
        foreach (var n in details.Networks.Where(n => !n.Refused))
        {
            if (CaipAsset.Parse(n.Asset) is not { } asset || TokenNamespaces.For(asset) is not { } ns) continue;
            var state = n.Quote switch
            {
                null => "unrequested",
                { ExpiresAt: { } at } when at <= now.ToUnixTimeSeconds() => "expired",
                { } q when q.AmountMsat != dueMsat => "stale",
                _ => "live"
            };
            var json = new JObject
            {
                ["id"] = n.OptionId, ["chain"] = asset.ChainId, ["namespace"] = asset.Namespace, ["label"] = ChainDirectory.Label(asset.ChainId),
                ["token"] = asset.AssetReference, ["state"] = state
            };
            if (state == "live")
            {
                var q = n.Quote!;
                var uri = ns.Uri(asset, q.Destination, q.Amount, details.Decimals);
                json["destination"] = q.Destination;
                json["amount"] = TokenAmount.Format(q.Amount, details.Decimals);
                json["baseUnits"] = q.Amount;
                json["expiresAt"] = q.ExpiresAt is { } e ? new JValue(e) : JValue.CreateNull();
                json["uri"] = uri is null ? JValue.CreateNull() : new JValue(uri);
                json["qr"] = uri ?? q.Destination;
            }
            networks.Add(json);
        }
        return new JObject
        {
            ["pmi"] = handler.PaymentMethodId.ToString(), ["code"] = handler.Code, ["decimals"] = details.Decimals,
            ["name"] = details.UnitName is null ? JValue.CreateNull() : new JValue(details.UnitName), ["networks"] = networks
        };
    }
}
