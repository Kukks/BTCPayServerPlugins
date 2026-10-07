using System.Net;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Rating;
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

    // A fresh host per call keeps the static ResolveCached cache from colliding across tests. The arkade
    // callback answers tark1qdestq, tark1qdestqq, ... on successive requests ('1' is not in the bech32 alphabet).
    static (string Host, FakeHttp Http, LnurlRailPaymentHandler Handler, StoreData Store) Lnurl(bool verify = true)
    {
        var host = "act" + Guid.NewGuid().ToString("N")[..8] + ".example";
        var callback = $"https://{host}/cb";
        var issued = 0;
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice",
                "{\"tag\":\"payRequest\",\"callback\":\"" + callback + "\",\"minSendable\":1000,\"maxSendable\":100000000,\"metadata\":\"[]\",\"paymentOptions\":[{\"id\":\"arkade\",\"type\":\"arkade\"}]}")
            .When(r => r.RequestUri!.ToString().StartsWith(callback + "?"), _ =>
            {
                var n = ++issued;
                var dest = "tark1qdest" + new string('q', n);
                var answer = new JObject
                {
                    ["paymentOption"] = "arkade", ["paymentDestination"] = dest, ["paymentURI"] = $"bitcoin:?ark={dest}",
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
                    ["verify"] = $"https://{host}/lnurl/verify/{n}", ["verifyBatch"] = $"https://{host}/lnurl/verifyBatch"
                };
                if (!verify) answer.Remove("verify");
                return (HttpStatusCode.OK, answer.ToString());
            });
        var store = Store();
        store.SetPaymentMethodConfig(PaymentMethodId.Parse("BTC-LN"), new JObject { ["connectionString"] = $"type=lnurl;value=alice@{host}" });
        return (host, http, new LnurlRailPaymentHandler(LnurlRails.Arkade, new FakeHttpClientFactory(http), Network.RegTest), store);
    }

    // 10 USD at 50,000 USD/BTC is 20,000 sat. The stored prompt defaults to the inactive one core leaves at creation.
    static InvoiceEntity Priced(PaymentPrompt? stored = null)
    {
        var invoice = Invoice();
        invoice.Price = 10m;
        invoice.ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(15);
        invoice.AddRate(new CurrencyPair("BTC", "USD"), 50_000m);
        invoice.SetPaymentPrompt(LnurlRails.Arkade.PaymentMethodId, stored ?? new PaymentPrompt { Currency = "BTC", Divisibility = 8, Inactive = true });
        return invoice;
    }

    static async Task<PaymentMethodContext> Activate(LnurlRailPaymentHandler handler, StoreData store, InvoiceEntity invoice)
    {
        var ctx = new PaymentMethodContext(store, store.GetStoreBlob(), new JObject(), handler, invoice, new InvoiceLogs());
        await ctx.BeforeFetchingRates();
        await handler.ConfigurePrompt(ctx);
        return ctx;
    }

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

    [Fact]
    public async Task A_rail_its_lnurl_cannot_verify_is_left_off_new_invoices()
    {
        var (_, http, handler, store) = Lnurl(verify: false);
        await Assert.ThrowsAsync<UnverifiableRailException>(() => Activate(handler, store, Priced()));
        var requests = http.Requests.Count;

        var (_, ctx) = await Create(store);
        Assert.False(ctx.Prompt.Inactive);
        await Assert.ThrowsAsync<UnverifiableRailException>(() => handler.ConfigurePrompt(ctx));
        Assert.Equal(requests, http.Requests.Count);
    }

    [Fact]
    public void A_rail_is_remembered_as_unverifiable_for_a_day()
    {
        var now = DateTimeOffset.UtcNow;
        UnverifiableRails.Remember("ttl@lnurl.example", "onchain", now);
        Assert.True(UnverifiableRails.Knows("ttl@lnurl.example", "onchain", now.AddHours(23)));
        Assert.False(UnverifiableRails.Knows("ttl@lnurl.example", "onchain", now.AddHours(25)));
        Assert.False(UnverifiableRails.Knows("ttl@lnurl.example", "arkade", now));
    }

    [Fact]
    public async Task Activation_requests_a_destination_and_records_it_on_the_prompt()
    {
        var (host, http, handler, store) = Lnurl();
        var ctx = await Activate(handler, store, Priced());
        var details = handler.ParsePaymentPromptDetails(ctx.Prompt.Details);
        Assert.Equal("tark1qdestq", ctx.Prompt.Destination);
        Assert.Equal(new[] { "tark1qdestq" }, ctx.TrackedDestinations);
        Assert.Equal(("arkade", "bitcoin:?ark=tark1qdestq", $"https://{host}/lnurl/verify/1", $"https://{host}/lnurl/verifyBatch", 20_000_000L),
            (details.OptionId, details.PaymentUri, details.Verify, details.VerifyBatch, details.AmountMsat));
        Assert.Empty(details.Superseded);
        Assert.Contains($"https://{host}/cb?amount=20000000&paymentOption=arkade", http.Requests);
    }

    [Fact]
    public async Task A_reissue_supersedes_the_previous_destination()
    {
        var (host, _, handler, store) = Lnurl();
        var stored = new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = "tark1qfirst",
            Details = JObject.FromObject(new LnurlRailPromptDetails
            {
                OptionId = "arkade", Verify = "https://lnurl.example/lnurl/verify/aa", AmountMsat = 20_000_000
            }, handler.Serializer)
        };
        var ctx = await Activate(handler, store, Priced(stored));
        var details = handler.ParsePaymentPromptDetails(ctx.Prompt.Details);
        Assert.Equal("tark1qdestq", ctx.Prompt.Destination);
        Assert.Equal($"https://{host}/lnurl/verify/1", details.Verify);
        Assert.Equal(new[] { ("tark1qfirst", "https://lnurl.example/lnurl/verify/aa", 20_000_000L) },
            details.Superseded.Select(s => (s.Destination, s.Verify, s.AmountMsat)));
    }

    [Fact]
    public async Task Activation_after_a_partial_payment_requests_only_the_remainder()
    {
        var (host, http, handler, store) = Lnurl();
        var invoice = Priced();
#pragma warning disable CS0618
        invoice.Payments = new List<PaymentEntity> { new() { Currency = "BTC", Value = 0.00008m, Status = PaymentStatus.Settled } };
#pragma warning restore CS0618
        invoice.UpdateTotals();
        var ctx = await Activate(handler, store, invoice);
        Assert.Equal(12_000_000L, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).AmountMsat);
        Assert.Contains($"https://{host}/cb?amount=12000000&paymentOption=arkade", http.Requests);
    }

    [Fact]
    public async Task Successive_activations_resolve_the_lnurl_once()
    {
        var (host, http, handler, store) = Lnurl();
        var invoice = Priced();
        await Activate(handler, store, invoice);
        await Activate(handler, store, invoice);
        // One cached resolution, then one pay-endpoint read per request.
        Assert.Equal(3, http.Requests.Count(u => u == $"https://{host}/.well-known/lnurlp/alice"));
    }

    [Fact]
    public void A_rail_prompt_reports_its_destinations_and_whether_its_agreed_amount_is_stale()
    {
        var stored = new PaymentPrompt
        {
            Currency = "BTC", Divisibility = 8, Destination = "tark1qcurrent",
            Details = JObject.FromObject(new LnurlRailPromptDetails
            {
                Verify = "https://lnurl.example/lnurl/verify/aa", AmountMsat = 20_000_000,
                Superseded = { new SupersededRailDestination { Destination = "tark1qold", Verify = "https://lnurl.example/lnurl/verify/bb", AmountMsat = 9_000_000 } }
            }, Handler.Serializer)
        };
        var invoice = Priced(stored);
        var prompt = invoice.GetPaymentPrompt(LnurlRails.Arkade.PaymentMethodId)!;
        Assert.False(Handler.NeedsReissue(prompt));
        Assert.Equal(new[] { "tark1qcurrent", "tark1qold" }, Handler.Tracked(invoice, prompt).Select(d => d.Destination));
        Assert.All(Handler.Tracked(invoice, prompt), d => Assert.Null(d.Asset));
    }
}
