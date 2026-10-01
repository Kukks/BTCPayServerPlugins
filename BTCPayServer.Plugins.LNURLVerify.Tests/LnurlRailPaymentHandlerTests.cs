using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

[Collection(RegistryCollection.Name)]
public class LnurlRailPaymentHandlerTests
{
    static readonly LnurlRailPaymentHandler Handler = new(LnurlRails.Arkade, new FakeHttpClientFactory(new FakeHttp()), Network.RegTest);

    static StoreData Store(bool chain = false, string? defaultPmi = null, params string[] excluded)
    {
        var strategies = new JObject
        {
            ["BTC-LN"] = new JObject { ["connectionString"] = "type=lnurl;value=alice@lnurl.example" },
            ["LNURL-ARKADE"] = new JObject()
        };
        if (chain) strategies["BTC-CHAIN"] = new JObject();
        var store = new StoreData { Id = "store", DerivationStrategies = strategies.ToString() };
        if (defaultPmi is not null) store.SetDefaultPaymentId(PaymentMethodId.Parse(defaultPmi));
        var blob = store.GetStoreBlob();
        foreach (var e in excluded) blob.SetExcluded(PaymentMethodId.Parse(e), true);
        store.SetStoreBlob(blob);
        return store;
    }

    static InvoiceEntity Invoice() => new() { Id = "inv", Currency = "USD", StoreId = "store" };

    // Mirrors core's creation order: SetLazyActivation, then BeforeFetchingRates (UIInvoiceController.cs:239,245).
    static async Task<(InvoiceEntity Invoice, PaymentMethodContext Context)> Create(StoreData store, InvoiceEntity? invoice = null)
    {
        invoice ??= Invoice();
        var creation = new InvoiceCreationContext(store, store.GetStoreBlob(), invoice, new InvoiceLogs(),
            new PaymentMethodHandlerDictionary(new IPaymentMethodHandler[] { Handler }), null);
        creation.SetLazyActivation(false);
        await creation.BeforeFetchingRates();
        return (invoice, creation.PaymentMethodContexts[LnurlRails.Arkade.PaymentMethodId]);
    }

    static PaymentMethodContext Activation(StoreData store, InvoiceEntity invoice) =>
        new(store, store.GetStoreBlob(), new JObject(), Handler, invoice, new InvoiceLogs());

    [Fact]
    public async Task The_prompt_is_inactive_at_creation_on_a_store_without_lazy_payment_methods()
    {
        var (_, ctx) = await Create(Store());
        Assert.True(ctx.Prompt.Inactive);
        Assert.Equal(("BTC", 8), (ctx.Prompt.Currency, ctx.Prompt.Divisibility));
    }

    [Fact]
    public async Task Activation_of_an_invoice_holding_the_prompt_leaves_it_active()
    {
        var store = Store();
        var invoice = Invoice();
        invoice.SetPaymentPrompt(LnurlRails.Arkade.PaymentMethodId, new PaymentPrompt { Currency = "BTC", Divisibility = 8, Inactive = true });
        var ctx = Activation(store, invoice);
        await ctx.BeforeFetchingRates();
        Assert.False(ctx.Prompt.Inactive);
    }

    [Theory]
    [InlineData(false, "BTC-LN")]
    [InlineData(true, "BTC-CHAIN")]
    public async Task Without_any_default_the_checkout_opens_on_a_core_rail(bool chain, string expected) =>
        Assert.Equal(expected, (await Create(Store(chain))).Invoice.DefaultPaymentMethod?.ToString());

    [Fact]
    public async Task A_store_or_invoice_default_is_kept()
    {
        Assert.Null((await Create(Store(defaultPmi: "BTC-LN"))).Invoice.DefaultPaymentMethod);
        var invoice = Invoice();
        invoice.DefaultPaymentMethod = LnurlRails.Arkade.PaymentMethodId;
        Assert.Equal(LnurlRails.Arkade.PaymentMethodId, (await Create(Store(), invoice)).Invoice.DefaultPaymentMethod);
    }

    [Fact]
    public async Task An_excluded_rail_sets_no_default() =>
        Assert.Null((await Create(Store(excluded: "LNURL-ARKADE"))).Invoice.DefaultPaymentMethod);

    [Fact]
    public async Task Activation_refuses_a_store_whose_lightning_is_not_an_lnurl()
    {
        var store = Store();
        store.SetPaymentMethodConfig(PaymentMethodId.Parse("BTC-LN"),
            new JObject { ["connectionString"] = "type=lnd-rest;server=https://lnd.example/" });
        var e = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Handler.ConfigurePrompt(Activation(store, Invoice())));
        Assert.Contains("not an LNURL", e.Message);
    }

    [Fact]
    public async Task Activation_refuses_a_top_up_invoice()
    {
        var invoice = Invoice();
        invoice.Type = InvoiceType.TopUp;
        var e = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Handler.ConfigurePrompt(Activation(Store(), invoice)));
        Assert.Contains("top-up", e.Message);
    }

    [Fact]
    public async Task Saving_an_active_prompt_tracks_every_destination_still_payable()
    {
        var invoice = Invoice();
        invoice.Id = "inv-track";
        invoice.MonitoringExpiration = DateTimeOffset.UtcNow.AddDays(1);
        var ctx = Activation(Store(), invoice);
        ctx.Prompt.Destination = "tark1qcurrent";
        ctx.Prompt.Details = JObject.FromObject(new LnurlRailPromptDetails
        {
            Verify = "https://lnurl.example/lnurl/verify/aa", AmountMsat = 4_000_000,
            Superseded = { new SupersededRailDestination { Destination = "tark1qold", Verify = "https://lnurl.example/lnurl/verify/bb", AmountMsat = 9_000_000 } }
        }, Handler.Serializer);
        try
        {
            await Handler.AfterSavingInvoice(ctx);
            var tracked = TrackedDestinationRegistry.All().Where(d => d.InvoiceId == "inv-track").OrderBy(d => d.AmountMsat).ToArray();
            Assert.Equal(new[] { ("tark1qcurrent", 4_000_000L), ("tark1qold", 9_000_000L) }, tracked.Select(d => (d.Destination, d.AmountMsat)));
            Assert.All(tracked, d => Assert.Equal(invoice.MonitoringExpiration, d.ExpiresAt));
        }
        finally
        {
            TrackedDestinationRegistry.Remove("https://lnurl.example/lnurl/verify/aa");
            TrackedDestinationRegistry.Remove("https://lnurl.example/lnurl/verify/bb");
        }
    }

    [Fact]
    public void A_reissued_destination_keeps_the_ones_it_replaces()
    {
        var previous = new PaymentPrompt
        {
            Destination = "tark1qsecond",
            Details = JObject.FromObject(new LnurlRailPromptDetails
            {
                Verify = "https://lnurl.example/lnurl/verify/bb", AmountMsat = 6_000_000,
                Superseded = { new SupersededRailDestination { Destination = "tark1qfirst", Verify = "https://lnurl.example/lnurl/verify/aa", AmountMsat = 9_000_000 } }
            }, Handler.Serializer)
        };
        Assert.Equal(new[] { "tark1qfirst", "tark1qsecond" }, Handler.SupersededBy(previous).Select(s => s.Destination));
        Assert.Empty(Handler.SupersededBy(null));
        Assert.Empty(Handler.SupersededBy(new PaymentPrompt { Inactive = true }));
    }

    [Fact]
    public void Amounts_convert_between_btc_and_whole_sat_msat()
    {
        Assert.Equal(123_456_000L, LnurlRailPaymentHandler.MsatOf(0.00123456m));
        Assert.Equal(0.00123456m, LnurlRailPaymentHandler.AmountBtc(123_456_000));
    }

    [Fact]
    public async Task The_lnurl_resolution_is_shared_and_cached()
    {
        var host = "cache" + Guid.NewGuid().ToString("N")[..8] + ".example";
        var url = $"https://{host}/.well-known/lnurlp/alice";
        var http = new FakeHttp().Map(url,
            "{\"tag\":\"payRequest\",\"callback\":\"https://" + host + "/cb\",\"minSendable\":1000,\"maxSendable\":1000000,\"metadata\":\"[]\"}");
        var ct = TestContext.Current.CancellationToken;
        var first = await LNURLVerifyConnectionStringHandler.ResolveCached(url, Network.RegTest, http.Client(), ct);
        var second = await LNURLVerifyConnectionStringHandler.ResolveCached(url, Network.RegTest, http.Client(), ct);
        Assert.Same(first, second);
        Assert.Single(http.Requests);
    }
}
