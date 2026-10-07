using BTCPayServer.Data;
using BTCPayServer.Payments;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlSetupSummaryTests
{
    static readonly TokenAssets Assets = TokenAssets.Parse("USDT").Assets;
    const string Lightning = "{\"id\":\"lightning\",\"type\":\"lightning\",\"minSendable\":5000}";
    const string Arkade = "{\"id\":\"arkade\",\"type\":\"arkade\"}";
    const string OnChain = "{\"id\":\"onchain\",\"type\":\"onchain\"}";
    const string ArbitrumUsdt = "{\"id\":\"usdt-arbitrum\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\",\"maxSendable\":50000000}";
    const string BaseEurc = "{\"id\":\"eurc-base\",\"type\":\"eip155\",\"asset\":\"eip155:8453/erc20:0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42\",\"unit\":\"EURC\"}";
    const string Unavailable = ",\"available\":false}";

    static string PayJson(string callback, params string[] options) =>
        "{\"tag\":\"payRequest\",\"callback\":\"" + callback + "\",\"minSendable\":1000,\"maxSendable\":100000000," +
        "\"units\":[{\"code\":\"USDT\",\"decimals\":6},{\"code\":\"EURC\",\"decimals\":6}]" +
        (options.Length == 0 ? "" : ",\"paymentOptions\":[" + string.Join(",", options) + "]") + "}";

    static StoreData Store(params string[] configured)
    {
        var store = new StoreData { Id = "store" };
        foreach (var pmi in configured) store.SetPaymentMethodConfig(PaymentMethodId.Parse(pmi), new JObject());
        return store;
    }

    static LnurlSetupSummary Summary(string pay, StoreData? store = null, string? saveError = null) =>
        LnurlSetupSummary.Build("alice@h.example",
            new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri("https://h.example/.well-known/lnurlp/alice"), null, null, "h.example"),
            JObject.Parse(pay), (saveError, true, null), store ?? Store(), Assets);

    static string[] Offered(LnurlSetupSummary summary) => summary.Options.Where(o => o.Offered).Select(o => o.Id!).ToArray();

    static string? Reason(LnurlSetupSummary summary, string id) => summary.Options.Single(o => o.Id == id).Reason;

    static string NewHost() => "setup" + Guid.NewGuid().ToString("N")[..8] + ".example";

    [Fact]
    public void A_plain_lightning_address_is_offered_as_lightning_within_its_bounds()
    {
        var summary = Summary(PayJson("https://h.example/cb"));

        var lightning = Assert.Single(summary.Options);
        Assert.Equal((null, "Lightning", 1000L, 100_000_000L, true),
            (lightning.Id, lightning.Label, lightning.MinSendable, lightning.MaxSendable, lightning.Offered));
        Assert.Equal(("h.example", "https://h.example/.well-known/lnurlp/alice", false), (summary.Domain, summary.Target, summary.Sends));
    }

    [Fact]
    public void Each_option_says_whether_checkout_offers_it_and_why_not()
    {
        var summary = Summary(PayJson("https://h.example/cb", Lightning, Arkade, OnChain.Replace("}", ",\"verifiable\":false}"),
            ArbitrumUsdt, BaseEurc, "{\"id\":\"liquid\",\"type\":\"liquid\"}"));

        Assert.Equal(new[] { "lightning", "arkade", "usdt-arbitrum" }, Offered(summary));
        Assert.Contains("not verifiable", Reason(summary, "onchain"));
        Assert.Contains("EURC", Reason(summary, "eurc-base"));
        Assert.Contains(TokenAssets.ConfigKey, Reason(summary, "eurc-base"));
        Assert.Contains("'liquid'", Reason(summary, "liquid"));
        var usdt = summary.Options.Single(o => o.Id == "usdt-arbitrum");
        Assert.Equal(("USDT on Arbitrum One", 1000L, 50_000_000L, "USDT"), (usdt.Label, usdt.MinSendable, usdt.MaxSendable, usdt.Unit));
        Assert.Equal((5000L, 100_000_000L), (summary.Options[0].MinSendable, summary.Options[0].MaxSendable));
    }

    [Fact]
    public void The_store_decides_which_rails_it_gets()
    {
        var pay = PayJson("https://h.example/cb", Lightning, Arkade, OnChain, ArbitrumUsdt);

        var withWallet = Summary(pay, Store("BTC-CHAIN"));
        Assert.Equal(new[] { "lightning", "arkade", "usdt-arbitrum" }, Offered(withWallet));
        Assert.Contains("on-chain wallet", Reason(withWallet, "onchain"));

        var withArkade = Summary(pay, Store("ARKADE"));
        Assert.Equal(new[] { "lightning", "usdt-arbitrum" }, Offered(withArkade));
        Assert.Contains("Arkade plugin", Reason(withArkade, "arkade"));
    }

    [Fact]
    public void An_option_reported_unavailable_is_not_offered_now()
    {
        var summary = Summary(PayJson("https://h.example/cb", Lightning.Replace("}", Unavailable),
            "{\"id\":\"ln-backup\",\"type\":\"lightning\"}", Arkade.Replace("}", Unavailable), ArbitrumUsdt.Replace("}", Unavailable)));

        Assert.Equal(new[] { "ln-backup" }, Offered(summary));
        Assert.All(summary.Options.Where(o => !o.Offered), o => Assert.Contains("unavailable", o.Reason));
    }

    [Fact]
    public void Nothing_is_offered_when_btcpay_would_refuse_to_save_the_lnurl()
    {
        var summary = Summary(PayJson("https://h.example/cb", Lightning, Arkade, ArbitrumUsdt), saveError: "no verify here");

        Assert.Equal("no verify here", summary.SaveError);
        Assert.Empty(Offered(summary));
        Assert.All(summary.Options, o => Assert.Contains("will not save", o.Reason));
    }

    [Fact]
    public void Nothing_is_offered_while_the_stores_lightning_is_disabled()
    {
        var store = Store();
        var blob = store.GetStoreBlob();
        blob.SetExcluded(LnurlRailProvisioning.Lightning, true);
        store.SetStoreBlob(blob);

        var summary = Summary(PayJson("https://h.example/cb", Lightning, Arkade, ArbitrumUsdt), store);

        Assert.Empty(Offered(summary));
        Assert.All(summary.Options, o => Assert.Contains("disabled", o.Reason));
    }

    [Fact]
    public async Task Read_probes_lightning_once_and_never_asks_a_rail_for_a_destination()
    {
        var host = NewHost();
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayJson($"https://{host}/cb", Lightning, Arkade))
            .Map($"https://{host}/cb?amount=5000&paymentOption=lightning",
                $"{{\"pr\":\"lnbcrt1\",\"verify\":\"https://{host}/verify/1\",\"verifyBatch\":\"https://{host}/verifyBatch\"}}");

        var summary = await LnurlSetupSummary.Read($"alice@{host}", Store(), Assets, Network.RegTest, http.Client(),
            TestContext.Current.CancellationToken);

        Assert.Equal((host, null, true, true), (summary.Domain, summary.SaveError, summary.Verify, summary.VerifyBatch));
        Assert.Equal(new[] { "lightning", "arkade" }, Offered(summary));
        Assert.Single(http.Requests, r => r.Contains("/cb?"));
    }

    [Fact]
    public async Task Read_reports_an_lnurl_whose_invoices_carry_no_verify()
    {
        var host = NewHost();
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayJson($"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", "{\"pr\":\"lnbcrt1\",\"verifyBatch\":\"https://" + host + "/verifyBatch\"}");

        var summary = await LnurlSetupSummary.Read($"alice@{host}", Store(), Assets, Network.RegTest, http.Client(),
            TestContext.Current.CancellationToken);

        Assert.Contains("LUD-21", summary.SaveError);
        Assert.Equal((false, false), (summary.Verify, summary.VerifyBatch));
        Assert.Empty(Offered(summary));
    }

    [Fact]
    public async Task Read_names_both_networks_when_the_lnurl_issues_another_networks_invoices()
    {
        var host = NewHost();
        var mutinynet = TestBolt11.Create(new Key(), 1000, new byte[32], prefix: "lntbs");
        var http = new FakeHttp()
            .Map($"https://{host}/.well-known/lnurlp/alice", PayJson($"https://{host}/cb", Arkade))
            .Map($"https://{host}/cb?amount=1000", $"{{\"pr\":\"{mutinynet}\",\"verify\":\"https://{host}/verify/1\"}}");

        var summary = await LnurlSetupSummary.Read($"alice@{host}", Store(), Assets, Network.RegTest, http.Client(),
            TestContext.Current.CancellationToken);

        Assert.True(summary.Verify);
        Assert.Contains("Signet", summary.SaveError);
        Assert.Contains("Regtest", summary.SaveError);
        Assert.Empty(Offered(summary));
    }
}
