using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Rating;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailCheckoutExtensionTests
{
    static readonly LnurlRailPaymentHandler Arkade = new(LnurlRails.Arkade, new FakeHttpClientFactory(new FakeHttp()), Network.RegTest);
    static readonly PaymentMethodId Ln = PaymentMethodId.Parse("BTC-LN");
    static readonly PaymentMethodId CoreLnurl = PaymentMethodId.Parse("BTC-LNURL");

    static LnurlRailCheckoutExtension Extension() =>
        new(Array.Empty<IPaymentLinkExtension>(), new PaymentMethodHandlerDictionary(new IPaymentMethodHandler[] { Arkade }),
            NullLogger<LnurlRailCheckoutExtension>.Instance);

    // A 25 USD invoice at 50 000 USD/BTC: 0.0005 BTC due on Lightning.
    static (CheckoutModelContext Context, CheckoutModel Model) Checkout(bool? arkadeActive = true, JToken? arkadeDetails = null, decimal paidBtc = 0m,
        bool lightningActive = true)
    {
        var invoice = new InvoiceEntity { Id = "inv", Currency = "USD", Price = 25m, StoreId = "store" };
        invoice.AddRate(new CurrencyPair("BTC", "USD"), 50_000m);
        invoice.SetPaymentPrompt(Ln, new PaymentPrompt
            { Currency = "BTC", Divisibility = 11, RateDivisibility = 8, Inactive = !lightningActive, Destination = lightningActive ? "lnbcrt5u1test" : null! });
        if (arkadeActive is { } active)
            invoice.SetPaymentPrompt(LnurlRails.Arkade.PaymentMethodId, new PaymentPrompt
            {
                Currency = "BTC", Divisibility = 8, Inactive = !active, Destination = active ? "tark1qdest" : null!,
                Details = active
                    ? arkadeDetails ?? JObject.FromObject(new LnurlRailPromptDetails
                        { Verify = "https://lnurl.example/lnurl/verify/aa", AmountMsat = 50_000_000 }, Arkade.Serializer)
                    : null!
            });
#pragma warning disable CS0618
        if (paidBtc > 0m)
            invoice.Payments = new List<PaymentEntity> { new() { Currency = "BTC", Value = paidBtc, Status = PaymentStatus.Settled } };
#pragma warning restore CS0618
        invoice.UpdateTotals();
        var model = new CheckoutModel
        {
            PaymentMethodId = "BTC-LN", PaymentMethodName = "Lightning",
            InvoiceBitcoinUrl = "lightning:lnbcrt5u1test", InvoiceBitcoinUrlQR = "lightning:LNBCRT5U1TEST",
            AvailablePaymentMethods = invoice.GetPaymentPrompts().Select(p => new CheckoutModel.AvailablePaymentMethod
                { PaymentMethodId = p.PaymentMethodId, PaymentMethodName = p.PaymentMethodId.ToString(), Displayed = true }).ToList()
        };
        var store = new StoreData { Id = "store" };
        return (new CheckoutModelContext(model, store, store.GetStoreBlob(), invoice, null!, invoice.GetPaymentPrompt(Ln)!, Arkade), model);
    }

    // BTC-LN failed at invoice creation, so core resolved the invoice to its own LNURL-pay method: no BTC-LN prompt exists.
    static (CheckoutModelContext Context, CheckoutModel Model) CoreLnurlCheckout()
    {
        var invoice = new InvoiceEntity { Id = "inv", Currency = "USD", Price = 25m, StoreId = "store" };
        invoice.AddRate(new CurrencyPair("BTC", "USD"), 50_000m);
        invoice.SetPaymentPrompt(CoreLnurl, new PaymentPrompt
            { Currency = "BTC", Divisibility = 11, RateDivisibility = 8, Destination = "lnurl1test" });
        invoice.SetPaymentPrompt(LnurlRails.Arkade.PaymentMethodId, new PaymentPrompt
            { Currency = "BTC", Divisibility = 8, Inactive = true, Destination = null! });
        invoice.UpdateTotals();
        var model = new CheckoutModel
        {
            PaymentMethodId = CoreLnurl.ToString(), PaymentMethodName = "Lightning (LNURL)",
            AvailablePaymentMethods = invoice.GetPaymentPrompts().Select(p => new CheckoutModel.AvailablePaymentMethod
                { PaymentMethodId = p.PaymentMethodId, PaymentMethodName = p.PaymentMethodId.ToString(), Displayed = true }).ToList()
        };
        var store = new StoreData { Id = "store" };
        return (new CheckoutModelContext(model, store, store.GetStoreBlob(), invoice, null!, invoice.GetPaymentPrompt(CoreLnurl)!, Arkade), model);
    }

    static (string Pmi, string Name)[] Pills(CheckoutModel model) =>
        model.AvailablePaymentMethods.Where(p => p.Displayed).Select(p => (p.PaymentMethodId.ToString(), p.PaymentMethodName)).ToArray();

    [Fact]
    public void A_lightning_tab_with_an_active_arkade_rail_becomes_one_bitcoin_tab()
    {
        var (context, model) = Checkout();
        Extension().ModifyCheckoutModel(context);
        Assert.Equal(new[] { ("BTC-LN", "Bitcoin") }, Pills(model));
        Assert.Equal(LnurlRailCheckoutExtension.ComponentName, model.CheckoutBodyComponentName);
        Assert.Equal("bitcoin:?amount=0.0005&lightning=lnbcrt5u1test&ark=tark1qdest", model.InvoiceBitcoinUrl);
        var rails = (JArray)model.AdditionalData["lnurlRails"];
        Assert.Equal(new[] { "Lightning", "Arkade" }, rails.Select(r => r["label"]!.Value<string>()));
        Assert.Equal("bitcoin:?amount=0.0005&ark=tark1qdest", rails[1]["uri"]!.Value<string>());
    }

    [Fact]
    public void An_inactive_rail_is_a_chip_without_a_uri_and_stays_out_of_the_qr()
    {
        var (context, model) = Checkout(arkadeActive: false);
        Extension().ModifyCheckoutModel(context);
        Assert.Equal("lightning:lnbcrt5u1test", model.InvoiceBitcoinUrl);
        var arkade = ((JArray)model.AdditionalData["lnurlRails"])[1];
        Assert.False(arkade["active"]!.Value<bool>());
        Assert.Equal(JTokenType.Null, arkade["uri"]!.Type);
    }

    [Fact]
    public void An_invoice_without_lnurl_rails_keeps_the_core_checkout()
    {
        var (context, model) = Checkout(arkadeActive: null);
        Extension().ModifyCheckoutModel(context);
        Assert.Null(model.CheckoutBodyComponentName);
        Assert.Equal(new[] { ("BTC-LN", "BTC-LN") }, Pills(model));
    }

    [Fact]
    public void A_rail_that_cannot_be_read_leaves_the_core_checkout_and_hides_only_the_rail_pill()
    {
        var (context, model) = Checkout(arkadeDetails: new JValue("not details"));
        Extension().ModifyCheckoutModel(context);
        Assert.Null(model.CheckoutBodyComponentName);
        Assert.Equal("lightning:lnbcrt5u1test", model.InvoiceBitcoinUrl);
        Assert.Equal(new[] { ("BTC-LN", "BTC-LN") }, Pills(model));
    }

    [Fact]
    public void A_rail_whose_agreed_amount_no_longer_matches_the_due_is_left_out()
    {
        var (context, model) = Checkout(paidBtc: 0.0002m);
        Assert.Equal(0.0003m, context.Prompt.Calculate().Due);
        Extension().ModifyCheckoutModel(context);
        var rails = (JArray)model.AdditionalData["lnurlRails"];
        Assert.Equal(new[] { "Lightning" }, rails.Select(r => r["label"]!.Value<string>()));
        Assert.Equal("lightning:lnbcrt5u1test", model.InvoiceBitcoinUrl);
    }

    [Fact]
    public void Re_skinning_keeps_pay_by_nfc_available()
    {
        var (context, model) = Checkout();
        Extension().ModifyCheckoutModel(context);
        Assert.True(model.OnChainWithLnInvoiceFallback);
    }

    [Fact]
    public void A_checkout_left_on_LNURL_pay_becomes_the_bitcoin_tab()
    {
        var (context, model) = CoreLnurlCheckout();
        Extension().ModifyCheckoutModel(context);
        Assert.Equal(LnurlRailCheckoutExtension.ComponentName, model.CheckoutBodyComponentName);
        Assert.Equal(new[] { ("BTC-LNURL", "Bitcoin") }, Pills(model));
        var arkade = ((JArray)model.AdditionalData["lnurlRails"]).Single();
        Assert.Equal("LNURL-ARKADE", arkade["pmi"]!.Value<string>());
        Assert.False(arkade["active"]!.Value<bool>());
    }

    [Fact]
    public void An_inactive_lightning_rail_does_not_claim_a_lightning_fallback()
    {
        var (context, model) = Checkout(lightningActive: false);
        Extension().ModifyCheckoutModel(context);
        Assert.Equal(LnurlRailCheckoutExtension.ComponentName, model.CheckoutBodyComponentName);
        Assert.False(model.OnChainWithLnInvoiceFallback);
    }

    [Fact]
    public void With_another_tab_open_the_bitcoin_methods_still_show_as_one_bitcoin_tab()
    {
        var (context, model) = Checkout();
        model.PaymentMethodId = "LNURL-USDT";
        model.AvailablePaymentMethods.Add(new CheckoutModel.AvailablePaymentMethod
            { PaymentMethodId = PaymentMethodId.Parse("LNURL-USDT"), PaymentMethodName = "USDT", Displayed = true });
        Extension().ModifyCheckoutModel(context);
        Assert.Equal(new[] { ("BTC-LN", "Bitcoin"), ("LNURL-USDT", "USDT") }, Pills(model));
        Assert.Null(model.CheckoutBodyComponentName);
    }
}
