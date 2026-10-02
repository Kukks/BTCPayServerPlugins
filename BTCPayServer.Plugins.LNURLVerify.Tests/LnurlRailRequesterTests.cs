using System.Net;
using BTCPayServer.Payments;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailRequesterTests
{
    static readonly Uri Pay = new("https://lnurl.example/.well-known/lnurlp/alice");
    const string Callback = "https://lnurl.example/.well-known/lnurlp/alice/callback";
    const string Dest = "tark1qdest";
    const string Both = "[{\"id\":\"lightning\",\"type\":\"lightning\"},{\"id\":\"arkade\",\"type\":\"arkade\"}]";
    static readonly DateTimeOffset InvoiceExpiry = DateTimeOffset.UtcNow.AddMinutes(15);

    static string PayRequest(string options = Both, string callback = Callback) =>
        "{\"tag\":\"payRequest\",\"callback\":\"" + callback + "\",\"minSendable\":1000,\"maxSendable\":100000000," +
        "\"metadata\":\"[]\",\"paymentOptions\":" + options + "}";

    static JObject Destination(Action<JObject>? edit = null)
    {
        var o = new JObject
        {
            ["status"] = "OK", ["paymentOption"] = "arkade", ["paymentDestination"] = Dest,
            ["paymentURI"] = "bitcoin:?ark=" + Dest + "&amount=0.0005",
            ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
            ["verify"] = "https://lnurl.example/lnurl/verify/00ff", ["verifyBatch"] = "https://lnurl.example/lnurl/verifyBatch"
        };
        edit?.Invoke(o);
        return o;
    }

    static FakeHttp Server(string payRequest, JObject destination, HttpStatusCode code = HttpStatusCode.OK) =>
        new FakeHttp().Map(Pay.ToString(), payRequest)
            .When(r => r.RequestUri!.ToString().StartsWith(Callback + "?"), _ => (code, destination.ToString()));

    static Task<RailDestination> Request(FakeHttp http, LnurlRail? rail = null, long msat = 50_000_000) =>
        LnurlRailRequester.Request(http.Client(), Pay, rail ?? LnurlRails.Arkade, msat, InvoiceExpiry, Network.RegTest,
            TestContext.Current.CancellationToken);

    static async Task<string> Refusal(FakeHttp http, LnurlRail? rail = null, long msat = 50_000_000) =>
        (await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Request(http, rail, msat))).Message;

    [Fact]
    public async Task Requests_the_option_by_id_and_returns_its_destination()
    {
        var http = Server(PayRequest(), Destination());
        var d = await Request(http);
        Assert.Equal(("arkade", Dest, "https://lnurl.example/lnurl/verify/00ff", "https://lnurl.example/lnurl/verifyBatch", 50_000_000L),
            (d.OptionId, d.Destination, d.Verify, d.VerifyBatch, d.AmountMsat));
        Assert.Contains(Callback + "?amount=50000000&paymentOption=arkade", http.Requests);
    }

    [Fact]
    public async Task An_option_the_lnurl_does_not_offer_is_refused_without_a_callback()
    {
        var http = Server(PayRequest("[{\"id\":\"lightning\",\"type\":\"lightning\"}]"), Destination());
        Assert.Contains("does not offer 'arkade'", await Refusal(http));
        Assert.DoesNotContain(http.Requests, u => u.StartsWith(Callback));
    }

    [Fact]
    public async Task An_unavailable_option_is_refused() =>
        Assert.Contains("currently unavailable",
            await Refusal(Server(PayRequest("[{\"id\":\"arkade\",\"type\":\"arkade\",\"available\":false}]"), Destination())));

    [Theory]
    [InlineData(999_000, false)]
    [InlineData(2_000_000, true)]
    [InlineData(3_000_000, false)]
    public async Task The_amount_must_lie_within_the_options_own_bounds(long msat, bool ok)
    {
        var http = Server(PayRequest("[{\"id\":\"arkade\",\"type\":\"arkade\",\"minSendable\":1000000,\"maxSendable\":2000000}]"), Destination());
        if (ok) Assert.Equal(msat, (await Request(http, msat: msat)).AmountMsat);
        else Assert.Contains("outside the 'arkade' bounds", await Refusal(http, msat: msat));
    }

    [Fact]
    public async Task Bounds_fall_back_to_the_top_level() =>
        Assert.Contains("outside", await Refusal(Server(PayRequest(), Destination()), msat: 200_000_000));

    [Fact]
    public async Task A_destination_without_verify_is_refused() =>
        Assert.Contains("settlement cannot be detected", await Refusal(Server(PayRequest(), Destination(o => o.Remove("verify")))));

    [Fact]
    public async Task A_verify_that_is_not_an_http_url_is_refused() =>
        Assert.Contains("settlement cannot be detected", await Refusal(Server(PayRequest(), Destination(o => o["verify"] = "/lnurl/verify/00ff"))));

    [Fact]
    public async Task An_answer_without_a_destination_is_refused() =>
        Assert.Contains("no destination", await Refusal(Server(PayRequest(), Destination(o => o.Remove("paymentDestination")))));

    [Fact]
    public async Task An_answer_for_another_option_is_refused() =>
        Assert.Contains("instead of 'arkade'", await Refusal(Server(PayRequest(), Destination(o => o["paymentOption"] = "onchain"))));

    [Fact]
    public async Task A_destination_expiring_before_the_invoice_is_refused() =>
        Assert.Contains("expires before the invoice", await Refusal(Server(PayRequest(),
            Destination(o => o["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()))));

    [Fact]
    public async Task A_destination_on_another_network_is_refused() =>
        Assert.Contains("not a valid Arkade destination", await Refusal(Server(PayRequest(),
            Destination(o => o["paymentDestination"] = "ark1qmainnetdestination"))));

    [Fact]
    public async Task An_onchain_destination_must_be_an_address_of_the_store_network()
    {
        const string options = "[{\"id\":\"onchain\",\"type\":\"onchain\"}]";
        var regtest = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var mainnet = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
        Action<JObject> onchain(string a) => o => { o["paymentOption"] = "onchain"; o["paymentDestination"] = a; };
        Assert.Equal(regtest, (await Request(Server(PayRequest(options), Destination(onchain(regtest))), LnurlRails.OnChain)).Destination);
        Assert.Contains("not a valid On-chain destination",
            await Refusal(Server(PayRequest(options), Destination(onchain(mainnet))), LnurlRails.OnChain));
    }

    [Fact]
    public async Task An_lnurl_error_is_a_refusal_carrying_its_reason() =>
        Assert.Contains("paymentOption arkade is disabled", await Refusal(Server(PayRequest(),
            new JObject { ["status"] = "ERROR", ["reason"] = "paymentOption arkade is disabled for this address" })));

    [Fact]
    public async Task A_throttled_callback_is_a_refusal() =>
        Assert.Contains("HTTP 429", await Refusal(Server(PayRequest(),
            new JObject { ["status"] = "ERROR", ["reason"] = "Too many requests" }, HttpStatusCode.TooManyRequests)));

    [Fact]
    public async Task A_plain_http_verify_under_an_https_callback_is_refused() =>
        Assert.Contains("plain-http verify", await Refusal(Server(PayRequest(),
            Destination(o => o["verify"] = "http://lnurl.example/lnurl/verify/00ff"))));

    [Fact]
    public async Task Plain_http_end_to_end_is_accepted()
    {
        var pay = new Uri("http://lnurl.example/.well-known/lnurlp/alice");
        const string callback = "http://lnurl.example/.well-known/lnurlp/alice/callback";
        const string verify = "http://lnurl.example/lnurl/verify/00ff";
        var http = new FakeHttp().Map(pay.ToString(), PayRequest(callback: callback))
            .When(r => r.RequestUri!.ToString().StartsWith(callback + "?"),
                _ => (HttpStatusCode.OK, Destination(o => o["verify"] = verify).ToString()));
        var d = await LnurlRailRequester.Request(http.Client(), pay, LnurlRails.Arkade, 50_000_000, InvoiceExpiry,
            Network.RegTest, TestContext.Current.CancellationToken);
        Assert.Equal(verify, d.Verify);
    }

    [Fact]
    public async Task A_plain_http_verifyBatch_for_an_https_verify_is_not_adopted() =>
        Assert.Null((await Request(Server(PayRequest(),
            Destination(o => o["verifyBatch"] = "http://lnurl.example/lnurl/verifyBatch")))).VerifyBatch);
}
