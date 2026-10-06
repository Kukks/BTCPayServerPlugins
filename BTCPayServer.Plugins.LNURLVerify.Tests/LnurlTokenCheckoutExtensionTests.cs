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

public class LnurlTokenCheckoutExtensionTests
{
    static readonly LnurlTokenPaymentHandler Handler = new("USDT", new FakeHttpClientFactory(new FakeHttp()), Network.RegTest, new TokenActivations());
    const string Arbitrum = "eip155:421614/erc20:0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB";
    const string Nile = "tron:0xcd8690dc/trc20:TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf";
    const string EvmTo = "0x1111111111111111111111111111111111111111";

    static TokenNetworkState Quoted(string id, string asset, string destination, long amountMsat = 20_000_000, long? expiresAt = null) => new()
    {
        OptionId = id, Asset = asset,
        Quote = new TokenQuoteState
        {
            Destination = destination, Amount = "12672000", AmountMsat = amountMsat, Verify = "https://lnurl.example/v/" + id,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
        }
    };

    // 10 USD at 50,000 USD/BTC: 20,000,000 msat due.
    static PaymentPrompt Prompt(params TokenNetworkState[] networks)
    {
        var invoice = new InvoiceEntity { Id = "inv", Currency = "USD", Price = 10m, StoreId = "store" };
        invoice.AddRate(new CurrencyPair("BTC", "USD"), 50_000m);
        invoice.SetPaymentPrompt(Handler.PaymentMethodId, new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8,
            Details = JObject.FromObject(new LnurlTokenPromptDetails { Unit = "USDT", Decimals = 6, UnitName = "Tether USD", Networks = networks.ToList() },
                Handler.Serializer)
        });
        invoice.UpdateTotals();
        return invoice.GetPaymentPrompt(Handler.PaymentMethodId)!;
    }

    static JObject OnlyNetwork(PaymentPrompt prompt) =>
        (JObject)LnurlTokenCheckoutExtension.Model(Handler, prompt, DateTimeOffset.UtcNow)["networks"]!.Single();

    [Fact]
    public void A_live_quote_shows_its_qr_uri_amount_and_recipient()
    {
        var model = LnurlTokenCheckoutExtension.Model(Handler, Prompt(Quoted("usdt-arbitrum", Arbitrum, EvmTo)), DateTimeOffset.UtcNow);
        Assert.Equal(("LNURL-USDT", "USDT", "Tether USD", 6), ((string)model["pmi"]!, (string)model["code"]!, (string)model["name"]!, (int)model["decimals"]!));
        var n = (JObject)model["networks"]!.Single();
        const string uri = "ethereum:0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB@421614/transfer?address=0x1111111111111111111111111111111111111111&uint256=12672000";
        Assert.Equal(("live", "Arbitrum Sepolia", "eip155", "eip155:421614", "12.672", uri, uri),
            ((string)n["state"]!, (string)n["label"]!, (string)n["namespace"]!, (string)n["chain"]!, (string)n["amount"]!, (string)n["uri"]!, (string)n["qr"]!));
        Assert.Equal((EvmTo, "0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB", "12672000"), ((string)n["destination"]!, (string)n["token"]!, (string)n["baseUnits"]!));
    }

    [Fact]
    public void A_tron_network_shows_its_recipient_as_the_qr()
    {
        var n = OnlyNetwork(Prompt(Quoted("usdt-tron", Nile, "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7")));
        Assert.Equal(JTokenType.Null, n["uri"]!.Type);
        Assert.Equal("TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7", (string)n["qr"]!);
    }

    [Fact]
    public void An_expired_quote_shows_no_destination()
    {
        var n = OnlyNetwork(Prompt(Quoted("usdt-arbitrum", Arbitrum, EvmTo, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds())));
        Assert.Equal("expired", (string)n["state"]!);
        Assert.Null(n["destination"]);
        Assert.Null(n["qr"]);
        Assert.Null(n["uri"]);
    }

    [Fact]
    public void A_quote_for_an_amount_no_longer_due_waits_for_its_reissue()
    {
        var n = OnlyNetwork(Prompt(Quoted("usdt-arbitrum", Arbitrum, EvmTo, amountMsat: 30_000_000)));
        Assert.Equal("stale", (string)n["state"]!);
        Assert.Null(n["destination"]);
    }

    [Fact]
    public void A_quote_that_is_both_expired_and_for_an_amount_no_longer_due_reads_as_expired()
    {
        var n = OnlyNetwork(Prompt(Quoted("usdt-arbitrum", Arbitrum, EvmTo, amountMsat: 30_000_000,
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds())));
        Assert.Equal("expired", (string)n["state"]!);
        Assert.Null(n["destination"]);
    }

    [Fact]
    public void Refused_networks_get_no_chip_and_unrequested_ones_do()
    {
        var n = OnlyNetwork(Prompt(new TokenNetworkState { OptionId = "usdt-arbitrum", Asset = Arbitrum },
            new TokenNetworkState { OptionId = "usdt-tron", Asset = Nile, Refused = true }));
        Assert.Equal(("usdt-arbitrum", "unrequested"), ((string)n["id"]!, (string)n["state"]!));
    }

    [Fact]
    public void A_network_whose_last_request_failed_reads_as_failed()
    {
        var n = OnlyNetwork(Prompt(new TokenNetworkState { OptionId = "usdt-arbitrum", Asset = Arbitrum, FailedAt = 1 }));
        Assert.Equal(("usdt-arbitrum", "failed"), ((string)n["id"]!, (string)n["state"]!));
        Assert.Null(n["destination"]);
    }

    [Fact]
    public void The_asset_tab_renders_with_the_token_component()
    {
        var prompt = Prompt(Quoted("usdt-arbitrum", Arbitrum, EvmTo));
        var model = new CheckoutModel { PaymentMethodId = "LNURL-USDT" };
        var store = new StoreData { Id = "store" };
        new LnurlTokenCheckoutExtension(Handler.PaymentMethodId, new PaymentMethodHandlerDictionary(new IPaymentMethodHandler[] { Handler }),
                NullLogger<LnurlTokenCheckoutExtension>.Instance)
            .ModifyCheckoutModel(new CheckoutModelContext(model, store, store.GetStoreBlob(), prompt.ParentEntity, null!, prompt, Handler));
        Assert.Equal(LnurlTokenCheckoutExtension.ComponentName, model.CheckoutBodyComponentName);
        Assert.Equal("LNURL-USDT", (string)((JObject)model.AdditionalData["lnurlToken"])["pmi"]!);
    }
}
