using System.Net;
using System.Web;
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
public class LnurlTokenPaymentHandlerTests
{
    static readonly PaymentMethodId Usdt = TokenAssets.PaymentMethodIdOf("USDT");
    static readonly string[] Solana = { "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM", "HwpBSwuyVKJi7d9kqqNexc54MS9i4BEDKDVDLeUVjZm8" };
    static readonly string[] Tron = { "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t" };
    const string ArbitrumAsset = "eip155:421614/erc20:0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB";

    // A fresh host per instance, since ResolveCached is static. Offers USDT on Arbitrum Sepolia, Solana devnet and Tron Nile at
    // 63,360 USDT/BTC; an EVM network's nth destination is 0x…n.
    sealed class Lnurl
    {
        public readonly string Host = "tok" + Guid.NewGuid().ToString("N")[..8] + ".example";
        public readonly FakeHttp Http = new();
        public readonly Dictionary<string, int> Calls = new();
        public readonly TokenActivations Activations = new();
        public readonly LnurlTokenPaymentHandler Handler;
        public readonly StoreData Store = new() { Id = "store" };
        public readonly long QuoteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        public string? WithoutVerify;
        public string? Failing;
        public string? WrongEcho;
        public string? BadExpiry;
        // A callback answers after its option's delay if the request is still alive; Timeout.InfiniteTimeSpan never answers.
        public readonly Dictionary<string, TimeSpan> Delays = new();

        public Lnurl(bool withBase = false, bool sameRecipient = false, bool usdt = true, TimeSpan? requestTimeout = null)
        {
            var callback = $"https://{Host}/cb";
            var options = new JArray
            {
                Option("usdt-arbitrum", ArbitrumAsset),
                Option("usdt-solana", "solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1/token:4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU"),
                Option("usdt-tron", "tron:0xcd8690dc/trc20:TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf")
            };
            if (withBase) options.Add(Option("usdt-base", "eip155:84532/erc20:0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB"));
            var pay = new JObject
            {
                ["tag"] = "payRequest", ["callback"] = callback, ["minSendable"] = 1000, ["maxSendable"] = 100_000_000_000, ["metadata"] = "[]",
                ["units"] = new JArray(new JObject { ["code"] = usdt ? "USDT" : "EURC", ["decimals"] = 6, ["name"] = "Tether USD" }),
                ["paymentOptions"] = options
            };
            Http.Map($"https://{Host}/.well-known/lnurlp/alice", pay.ToString())
                .WhenAsync(r => r.RequestUri!.ToString().StartsWith(callback + "?"), async (r, ct) =>
                {
                    var query = HttpUtility.ParseQueryString(r.RequestUri!.Query);
                    var option = query["paymentOption"]!;
                    int n;
                    lock (Calls) n = Calls[option] = Calls.GetValueOrDefault(option) + 1;
                    if (Delays.TryGetValue(option, out var delay)) await Task.Delay(delay, ct);
                    if (option == Failing) return (HttpStatusCode.InternalServerError, "{}");
                    var answer = new JObject
                    {
                        ["paymentOption"] = option, ["paymentDestination"] = Destination(option, sameRecipient ? 1 : n),
                        ["paymentQuote"] = new JObject
                        {
                            ["payment"] = new JObject { ["amount"] = (long.Parse(query["amount"]!) * 63_360 / 100_000).ToString(), ["unit"] = "USDT" },
                            ["expiresAt"] = QuoteExpiresAt
                        },
                        ["verify"] = $"https://{Host}/lnurl/verify/{option}/{n}", ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()
                    };
                    if (option == WithoutVerify) answer.Remove("verify");
                    if (option == WrongEcho) answer["paymentOption"] = option + "-other";
                    if (option == BadExpiry) answer["expiresAt"] = 1_790_000_600_000L;
                    return (HttpStatusCode.OK, answer.ToString());
                });
            Handler = new LnurlTokenPaymentHandler("USDT", new FakeHttpClientFactory(Http), Network.RegTest, Activations)
                { RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20) };
            Store.SetPaymentMethodConfig(PaymentMethodId.Parse("BTC-LN"), new JObject { ["connectionString"] = $"type=lnurl;value=alice@{Host}" });
            Store.SetPaymentMethodConfig(Usdt, new JObject());
        }

        static JObject Option(string id, string asset) => new() { ["id"] = id, ["type"] = asset.Split(':')[0], ["asset"] = asset, ["unit"] = "USDT" };

        static string Destination(string option, int n) =>
            option is "usdt-arbitrum" or "usdt-base" ? "0x" + n.ToString("x40") : option == "usdt-solana" ? Solana[n - 1] : Tron[n - 1];

        // 10 USD at 50,000 USD/BTC is 20,000 sat. The stored prompt is the inactive one core leaves at creation.
        public InvoiceEntity Invoice()
        {
            var invoice = new InvoiceEntity
            {
                Id = "inv-" + Guid.NewGuid().ToString("N")[..8], Currency = "USD", StoreId = "store", Price = 10m,
                ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(15), MonitoringExpiration = DateTimeOffset.UtcNow.AddDays(1)
            };
            invoice.AddRate(new CurrencyPair("BTC", "USD"), 50_000m);
            invoice.SetPaymentPrompt(Usdt, new PaymentPrompt { Currency = "BTC", Divisibility = 8, Inactive = true });
            return invoice;
        }

        // What core's activator does: a fresh context, whose prompt then becomes the invoice's.
        public async Task<PaymentMethodContext> Activate(InvoiceEntity invoice, params string[] networks)
        {
            PaymentMethodContext? ctx = null;
            await Activations.Run(invoice.Id, Usdt, networks, async () =>
            {
                ctx = new PaymentMethodContext(Store, Store.GetStoreBlob(), new JObject(), Handler, invoice, new InvoiceLogs());
                await ctx.BeforeFetchingRates();
                await Handler.ConfigurePrompt(ctx);
                return true;
            });
            invoice.SetPaymentPrompt(Usdt, ctx!.Prompt);
            return ctx;
        }

        public LnurlTokenPromptDetails Details(InvoiceEntity invoice) => Handler.ParsePaymentPromptDetails(invoice.GetPaymentPrompt(Usdt)!.Details);

        public int CallsTo(string option)
        {
            lock (Calls) return Calls.GetValueOrDefault(option);
        }
    }

    [Fact]
    public async Task The_prompt_is_inactive_at_creation_and_the_checkout_opens_on_a_core_rail()
    {
        var lnurl = new Lnurl();
        var invoice = new InvoiceEntity { Id = "inv", Currency = "USD", StoreId = "store" };
        var creation = new InvoiceCreationContext(lnurl.Store, lnurl.Store.GetStoreBlob(), invoice, new InvoiceLogs(),
            new PaymentMethodHandlerDictionary(new IPaymentMethodHandler[] { lnurl.Handler }), null);
        creation.SetLazyActivation(false);
        await creation.BeforeFetchingRates();
        Assert.True(creation.PaymentMethodContexts[Usdt].Prompt.Inactive);
        Assert.Equal("BTC-LN", invoice.DefaultPaymentMethod?.ToString());
        Assert.Empty(lnurl.Http.Requests);
    }

    [Fact]
    public async Task Opening_the_tab_lists_the_networks_without_requesting_any()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        var ctx = await lnurl.Activate(invoice);
        Assert.False(ctx.Prompt.Inactive);
        Assert.Null(ctx.Prompt.Destination);
        var details = lnurl.Details(invoice);
        Assert.Equal(("USDT", 6, "Tether USD"), (details.Unit, details.Decimals, details.UnitName));
        Assert.Equal(new[] { "usdt-arbitrum", "usdt-solana", "usdt-tron" }, details.Networks.Select(n => n.OptionId));
        Assert.All(details.Networks, n => Assert.Null(n.Quote));
        Assert.Empty(lnurl.Calls);
    }

    [Fact]
    public async Task A_requested_network_gets_a_quote_and_becomes_the_destination()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        var ctx = await lnurl.Activate(invoice, "usdt-arbitrum");
        var quote = lnurl.Details(invoice).Networks[0].Quote!;
        Assert.Equal(("0x0000000000000000000000000000000000000001", "12672000", 20_000_000L, $"https://{lnurl.Host}/lnurl/verify/usdt-arbitrum/1", lnurl.QuoteExpiresAt),
            (quote.Destination, quote.Amount, quote.AmountMsat, quote.Verify, quote.ExpiresAt!.Value));
        Assert.Equal(quote.Destination, ctx.Prompt.Destination);
        Assert.Equal(new[] { quote.Destination }, ctx.TrackedDestinations);
        Assert.Contains($"https://{lnurl.Host}/cb?amount=20000000&paymentOption=usdt-arbitrum", lnurl.Http.Requests);
        Assert.Equal((1, 0), (lnurl.CallsTo("usdt-arbitrum"), lnurl.CallsTo("usdt-solana")));
    }

    [Fact]
    public async Task Each_network_keeps_its_own_quote()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-arbitrum");
        var ctx = await lnurl.Activate(invoice, "usdt-solana");
        Assert.Equal(new[] { "0x0000000000000000000000000000000000000001", Solana[0], null },
            lnurl.Details(invoice).Networks.Select(n => n.Quote?.Destination));
        Assert.Equal(Solana[0], ctx.Prompt.Destination);
        Assert.Equal((1, 1), (lnurl.CallsTo("usdt-arbitrum"), lnurl.CallsTo("usdt-solana")));
    }

    [Fact]
    public async Task Networks_sharing_one_recipient_keep_their_own_quotes()
    {
        var lnurl = new Lnurl(withBase: true, sameRecipient: true);
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-arbitrum", "usdt-base");
        var tracked = lnurl.Handler.Tracked(invoice, invoice.GetPaymentPrompt(Usdt)!).ToArray();
        Assert.Equal(new[]
        {
            ("0x0000000000000000000000000000000000000001", ArbitrumAsset),
            ("0x0000000000000000000000000000000000000001", "eip155:84532/erc20:0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB")
        }, tracked.Select(d => (d.Destination, d.Asset!)));
        Assert.Equal(2, tracked.Select(d => d.VerifyUrl).Distinct().Count());
    }

    [Fact]
    public async Task An_expired_quote_is_superseded_only_when_requested_again()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-arbitrum");
        var details = lnurl.Details(invoice);
        details.Networks[0].Quote!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        invoice.GetPaymentPrompt(Usdt)!.Details = JObject.FromObject(details, lnurl.Handler.Serializer);

        await lnurl.Activate(invoice);
        Assert.Equal(1, lnurl.CallsTo("usdt-arbitrum"));
        await lnurl.Activate(invoice, "usdt-arbitrum");
        Assert.Equal(2, lnurl.CallsTo("usdt-arbitrum"));

        var after = lnurl.Details(invoice);
        Assert.Equal("0x0000000000000000000000000000000000000002", after.Networks[0].Quote!.Destination);
        var superseded = Assert.Single(after.Superseded);
        Assert.Equal(("0x0000000000000000000000000000000000000001", ArbitrumAsset), (superseded.Destination, superseded.Asset));
        Assert.Equal(2, lnurl.Handler.Tracked(invoice, invoice.GetPaymentPrompt(Usdt)!).Count());
    }

    [Fact]
    public async Task A_partial_payment_reissues_only_the_quoted_networks()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-arbitrum");
#pragma warning disable CS0618
        invoice.Payments = new List<PaymentEntity> { new() { Currency = "BTC", Value = 0.00008m, Status = PaymentStatus.Settled } };
#pragma warning restore CS0618
        invoice.UpdateTotals();
        Assert.True(lnurl.Handler.NeedsReissue(invoice.GetPaymentPrompt(Usdt)!));

        await lnurl.Activate(invoice);
        var details = lnurl.Details(invoice);
        Assert.Equal(("7603200", 12_000_000L), (details.Networks[0].Quote!.Amount, details.Networks[0].Quote!.AmountMsat));
        Assert.Equal(20_000_000L, Assert.Single(details.Superseded).AmountMsat);
        Assert.Equal((2, 0, 0), (lnurl.CallsTo("usdt-arbitrum"), lnurl.CallsTo("usdt-solana"), lnurl.CallsTo("usdt-tron")));
        Assert.False(lnurl.Handler.NeedsReissue(invoice.GetPaymentPrompt(Usdt)!));
    }

    [Fact]
    public async Task A_network_the_lnurl_stops_offering_stays_tracked()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-tron");
        var url = $"https://{lnurl.Host}/.well-known/lnurlp/alice";
        var pay = JObject.Parse(lnurl.Http.Routes[url].Body);
        pay["paymentOptions"]!.Last!.Remove();
        lnurl.Http.Routes[url] = (HttpStatusCode.OK, pay.ToString());

        await lnurl.Activate(invoice);
        var details = lnurl.Details(invoice);
        Assert.Equal(new[] { "usdt-arbitrum", "usdt-solana" }, details.Networks.Select(n => n.OptionId));
        Assert.Equal(Tron[0], Assert.Single(details.Superseded).Destination);
        Assert.Contains(lnurl.Handler.Tracked(invoice, invoice.GetPaymentPrompt(Usdt)!), d => d.Destination == Tron[0]);
    }

    [Fact]
    public async Task A_network_refused_as_unverifiable_is_hidden_and_not_asked_again()
    {
        var lnurl = new Lnurl { WithoutVerify = "usdt-solana" };
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-solana");
        var solana = lnurl.Details(invoice).Networks[1];
        Assert.True(solana.Refused);
        Assert.Null(solana.Quote);

        await lnurl.Activate(invoice, "usdt-solana");
        var next = lnurl.Invoice();
        await lnurl.Activate(next, "usdt-solana");
        Assert.Equal(1, lnurl.CallsTo("usdt-solana"));
        Assert.True(lnurl.Details(next).Networks[1].Refused);
    }

    [Fact]
    public async Task A_network_refused_by_a_bad_answer_is_hidden_and_not_asked_again()
    {
        var lnurl = new Lnurl { WrongEcho = "usdt-arbitrum" };
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-arbitrum");
        Assert.True(lnurl.Details(invoice).Networks[0].Refused);
        Assert.Equal(1, lnurl.CallsTo("usdt-arbitrum"));

        lnurl.WrongEcho = null;
        await lnurl.Activate(invoice, "usdt-arbitrum");
        var arbitrum = lnurl.Details(invoice).Networks[0];
        Assert.True(arbitrum.Refused);
        Assert.Null(arbitrum.Quote);
        Assert.Equal(1, lnurl.CallsTo("usdt-arbitrum"));
    }

    [Fact]
    public async Task A_network_whose_request_fails_is_offered_again_and_quotes_on_a_retry()
    {
        var lnurl = new Lnurl { Failing = "usdt-arbitrum" };
        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice, "usdt-arbitrum", "usdt-solana", "usdt-tron");
        var networks = lnurl.Details(invoice).Networks;
        Assert.False(networks[0].Refused);
        Assert.NotNull(networks[0].FailedAt);
        Assert.Equal(new[] { Solana[0], Tron[0] }, networks.Skip(1).Select(n => n.Quote!.Destination));

        lnurl.Failing = null;
        await lnurl.Activate(invoice, "usdt-arbitrum");
        var arbitrum = lnurl.Details(invoice).Networks[0];
        Assert.Equal("0x0000000000000000000000000000000000000002", arbitrum.Quote!.Destination);
        Assert.Null(arbitrum.FailedAt);
        Assert.Equal(2, lnurl.CallsTo("usdt-arbitrum"));
    }

    [Fact]
    public async Task A_network_that_does_not_answer_in_time_fails_alone_and_stays_offered()
    {
        var budget = TimeSpan.FromSeconds(2);
        var lnurl = new Lnurl(requestTimeout: budget);
        lnurl.Delays["usdt-arbitrum"] = budget * 0.6;
        lnurl.Delays["usdt-solana"] = budget * 0.6;
        lnurl.Delays["usdt-tron"] = Timeout.InfiniteTimeSpan;
        var invoice = lnurl.Invoice();

        var ctx = await lnurl.Activate(invoice, "usdt-arbitrum", "usdt-solana", "usdt-tron");

        var networks = lnurl.Details(invoice).Networks;
        Assert.Equal(new[] { "0x0000000000000000000000000000000000000001", Solana[0], null }, networks.Select(n => n.Quote?.Destination));
        Assert.Equal(new[] { "0x0000000000000000000000000000000000000001", Solana[0] }, ctx.TrackedDestinations);
        Assert.False(networks[2].Refused);
        Assert.NotNull(networks[2].FailedAt);
        Assert.Contains(ctx.Logs.InvoiceLogs.ToList(),
            l => l.Severity == InvoiceEventData.EventSeverity.Warning && l.Log.Contains("usdt-tron: the LNURL did not answer in time"));
    }

    [Fact]
    public async Task A_network_whose_destination_expiry_cannot_be_read_is_refused_alone()
    {
        var lnurl = new Lnurl { BadExpiry = "usdt-solana" };
        var invoice = lnurl.Invoice();

        var ctx = await lnurl.Activate(invoice, "usdt-arbitrum", "usdt-solana", "usdt-tron");

        var networks = lnurl.Details(invoice).Networks;
        Assert.Equal(new[] { "0x0000000000000000000000000000000000000001", null, Tron[0] }, networks.Select(n => n.Quote?.Destination));
        Assert.True(networks[1].Refused);
        Assert.Equal(new[] { "0x0000000000000000000000000000000000000001", Tron[0] }, ctx.TrackedDestinations);
        Assert.Contains(ctx.Logs.InvoiceLogs.ToList(),
            l => l.Severity == InvoiceEventData.EventSeverity.Warning && l.Log.Contains("usdt-solana: the LNURL returned an expiry that could not be read"));
    }

    [Fact]
    public async Task An_lnurl_offering_no_network_for_the_asset_refuses_the_tab()
    {
        var lnurl = new Lnurl(usdt: false);
        var e = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => lnurl.Activate(lnurl.Invoice()));
        Assert.Contains("no network for USDT", e.Message);
    }

    [Fact]
    public async Task The_tab_lists_only_the_networks_of_its_own_asset()
    {
        var lnurl = new Lnurl();
        var url = $"https://{lnurl.Host}/.well-known/lnurlp/alice";
        var pay = JObject.Parse(lnurl.Http.Routes[url].Body);
        ((JArray)pay["units"]!).Add(new JObject { ["code"] = "USDC", ["decimals"] = 6, ["name"] = "USD Coin" });
        var usdc = (JObject)pay["paymentOptions"]![0]!.DeepClone();
        usdc["id"] = "usdc-arbitrum";
        usdc["asset"] = "eip155:421614/erc20:0x75faf114eafb1BDbe2F0316DF893fd58CE46AA4d";
        usdc["unit"] = "USDC";
        ((JArray)pay["paymentOptions"]!).Add(usdc);
        lnurl.Http.Routes[url] = (HttpStatusCode.OK, pay.ToString());
        // It parses, so only the handler's unit filter can keep it off the USDT tab.
        Assert.Contains("usdc-arbitrum", TokenOption.Parse(pay).Select(o => o.Id));

        var invoice = lnurl.Invoice();
        await lnurl.Activate(invoice);
        Assert.Equal(new[] { "usdt-arbitrum", "usdt-solana", "usdt-tron" }, lnurl.Details(invoice).Networks.Select(n => n.OptionId));
    }

    [Fact]
    public async Task Saving_the_prompt_tracks_every_quote()
    {
        var lnurl = new Lnurl();
        var invoice = lnurl.Invoice();
        var ctx = await lnurl.Activate(invoice, "usdt-arbitrum", "usdt-tron");
        try
        {
            await lnurl.Handler.AfterSavingInvoice(ctx);
            var tracked = TrackedDestinationRegistry.All().Where(d => d.InvoiceId == invoice.Id).OrderBy(d => d.Destination, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { ("0x0000000000000000000000000000000000000001", "LNURL-USDT"), (Tron[0], "LNURL-USDT") },
                tracked.Select(d => (d.Destination, d.PaymentMethodId)));
            Assert.All(tracked, d => Assert.Equal(invoice.MonitoringExpiration, d.ExpiresAt));
        }
        finally
        {
            foreach (var d in TrackedDestinationRegistry.All().Where(d => d.InvoiceId == invoice.Id))
                TrackedDestinationRegistry.Remove(d.VerifyUrl);
        }
    }
}
