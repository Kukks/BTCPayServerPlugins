using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

[Collection(RegistryCollection.Name)]
public class LNURLReceiverTests
{
    [Fact]
    public void Preimage_validation_matches_sha256()
    {
        var preimage = new string('0', 64); // 32 zero bytes
        var hash = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"; // sha256(32*0x00)
        Assert.True(LNURLReceiver.IsValidPreimage(preimage, hash));
        Assert.False(LNURLReceiver.IsValidPreimage(preimage, new string('1', 64)));
        Assert.False(LNURLReceiver.IsValidPreimage("xyz", hash));
        Assert.False(LNURLReceiver.IsValidPreimage(null, hash));
    }

    [Fact]
    public async Task GetInvoice_transient_transport_error_returns_unpaid_not_null()
    {
        var host = "rx.example";
        var hash = new string('a', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}", "{}", HttpStatusCode.InternalServerError);
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http.Client(), NullLogger.Instance);

        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.NotNull(inv);
        Assert.Equal(LightningInvoiceStatus.Unpaid, inv!.Status);
        TrackedInvoiceRegistry.Remove(hash);
    }

    [Fact]
    public async Task GetInvoice_error_status_returns_null()
    {
        var host = "rx2.example";
        var hash = new string('b', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}", "{\"status\":\"ERROR\",\"reason\":\"nope\"}");
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http.Client(), NullLogger.Instance);

        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.Null(inv);
        TrackedInvoiceRegistry.Remove(hash);
    }

    const string PayMeta =
        "{\"tag\":\"payRequest\",\"callback\":\"{CB}\",\"minSendable\":1000,\"maxSendable\":100000000,\"metadata\":\"[[\\\"text/plain\\\",\\\"x\\\"]]\"}";

    // Canonical BOLT#11 spec example (mainnet, 250,000,000 msat) — parses offline.
    const string SpecBolt11 =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    const string SpecHash = "0001020304050607080900010203040506070809000102030405060708090102";

    static string UniqueHost(string prefix) => prefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".example";
    static string NewHash() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    static LNURLReceiver Receiver(string host, FakeHttp http, Network network) =>
        new(new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host),
            network, http.Client(), NullLogger.Instance);

    static string PayJson(string cb, long maxSendable = 100_000_000, string? options = null) =>
        "{\"tag\":\"payRequest\",\"callback\":\"" + cb + "\",\"minSendable\":1000,\"maxSendable\":" + maxSendable +
        ",\"metadata\":\"[[\\\"text/plain\\\",\\\"x\\\"]]\"" + (options is null ? "" : ",\"paymentOptions\":" + options) + "}";

    // mutinynet is a signet, so its invoices carry lntbs.
    static string SignetInvoice() => TestBolt11.Create(new Key(), 1000, RandomNumberGenerator.GetBytes(32), prefix: "lntbs");
    static string RegtestInvoice(long msat) => TestBolt11.Create(new Key(), msat, RandomNumberGenerator.GetBytes(32));

    static string SpecCallback(string host, string extra = "") =>
        $"{{\"pr\":\"{SpecBolt11}\",\"verify\":\"https://{host}/verify/{SpecHash}\"{extra}}}";

    [Fact]
    public async Task CreateInvoice_requests_the_lightning_option_within_its_own_bounds()
    {
        var host = UniqueHost("opt");
        // The top-level max (100,000,000 msat) is below the spec invoice; only the option's own max admits it.
        var options = "[{\"id\":\"ln-main\",\"type\":\"lightning\",\"maxSendable\":250000000},{\"id\":\"arkade\",\"type\":\"arkade\"}]";
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb", options: options))
            .Map($"https://{host}/cb?amount=250000000&paymentOption=ln-main", SpecCallback(host));

        var inv = await Receiver(host, http, Network.Main)
            .CreateInvoice(LightMoney.MilliSatoshis(250_000_000), "x", null, TestContext.Current.CancellationToken);

        Assert.Equal(SpecHash, inv.PaymentHash);
        TrackedInvoiceRegistry.Remove(SpecHash);
    }

    [Fact]
    public async Task CreateInvoice_refuses_while_the_lightning_option_is_unavailable()
    {
        var host = UniqueHost("optoff");
        var http = new FakeHttp().Map($"https://{host}/pay",
            PayJson($"https://{host}/cb", 250_000_000, "[{\"id\":\"lightning\",\"type\":\"lightning\",\"available\":false}]"));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => Receiver(host, http, Network.Main)
            .CreateInvoice(LightMoney.MilliSatoshis(250_000_000), "x", null, TestContext.Current.CancellationToken));

        Assert.Contains("unavailable", ex.Message);
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[{\"id\":\"arkade\",\"type\":\"arkade\"}]")]
    public async Task CreateInvoice_without_a_lightning_option_sends_a_plain_LUD06_request(string options)
    {
        var host = UniqueHost("plain");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb", 250_000_000, options == "" ? null : options))
            .Map($"https://{host}/cb?amount=250000000", SpecCallback(host));

        var inv = await Receiver(host, http, Network.Main)
            .CreateInvoice(LightMoney.MilliSatoshis(250_000_000), "x", null, TestContext.Current.CancellationToken);

        Assert.Equal(SpecHash, inv.PaymentHash);
        TrackedInvoiceRegistry.Remove(SpecHash);
    }

    [Fact]
    public async Task CheckVerifySupport_probes_with_the_lightning_options_minimum()
    {
        var host = UniqueHost("probe");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb",
                options: "[{\"id\":\"lightning\",\"type\":\"lightning\",\"minSendable\":5000}]"))
            .Map($"https://{host}/cb?amount=5000&paymentOption=lightning",
                $"{{\"pr\":\"{RegtestInvoice(5000)}\",\"verify\":\"https://{host}/verify/abc\"}}");

        Assert.Null(await Receiver(host, http, Network.RegTest).CheckVerifySupport(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"pr\":42,")]
    [InlineData("\"pr\":\"lnbcrt1notaninvoice\",")]
    public async Task CheckVerifySupport_refuses_a_probe_answer_without_a_readable_invoice(string pr)
    {
        var host = UniqueHost("noinv");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", "{" + pr + $"\"verify\":\"https://{host}/verify/abc\"}}");

        Assert.Contains("invoice", await Receiver(host, http, Network.RegTest).CheckVerifySupport(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckVerifySupport_names_both_networks_when_the_probe_invoice_is_another_networks()
    {
        var host = UniqueHost("probenet");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", $"{{\"pr\":\"{SignetInvoice()}\",\"verify\":\"https://{host}/verify/abc\"}}");

        var err = await Receiver(host, http, Network.RegTest).CheckVerifySupport(TestContext.Current.CancellationToken);

        Assert.Contains("Signet", err);
        Assert.Contains("Regtest", err);
        Assert.Null(await Receiver(host, http, NBitcoin.Bitcoin.Instance.Signet).CheckVerifySupport(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateInvoice_names_both_networks_instead_of_an_invalid_prefix()
    {
        var host = UniqueHost("invnet");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", $"{{\"pr\":\"{SignetInvoice()}\",\"verify\":\"https://{host}/verify/abc\"}}");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Receiver(host, http, Network.RegTest)
            .CreateInvoice(LightMoney.MilliSatoshis(1000), "x", null, TestContext.Current.CancellationToken));

        Assert.Contains("Signet", ex.Message);
        Assert.Contains("Regtest", ex.Message);
    }

    [Fact]
    public void Only_a_known_invoice_prefix_is_named_as_another_network()
    {
        Assert.Contains("Signet", LNURLReceiver.NetworkMismatch("lntbs10n1x", Network.TestNet));
        Assert.Null(LNURLReceiver.NetworkMismatch("not-an-invoice", Network.RegTest));
    }

    [Fact]
    public async Task CheckVerifySupport_reports_an_unavailable_lightning_option()
    {
        var host = UniqueHost("probeoff");
        var http = new FakeHttp().Map($"https://{host}/pay",
            PayJson($"https://{host}/cb", options: "[{\"id\":\"lightning\",\"type\":\"lightning\",\"available\":false}]"));

        var err = await Receiver(host, http, Network.RegTest).CheckVerifySupport(TestContext.Current.CancellationToken);

        Assert.Contains("unavailable", err);
    }

    [Fact]
    public async Task CreateInvoice_tracks_the_advertised_verifyBatch_url()
    {
        var host = UniqueHost("vb");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb", 250_000_000))
            .Map($"https://{host}/cb?amount=250000000", SpecCallback(host, $",\"verifyBatch\":\"https://{host}/lnurl/verifyBatch\""));

        await Receiver(host, http, Network.Main)
            .CreateInvoice(LightMoney.MilliSatoshis(250_000_000), "x", null, TestContext.Current.CancellationToken);

        Assert.True(TrackedInvoiceRegistry.TryGet(SpecHash, out var t));
        Assert.Equal($"https://{host}/lnurl/verifyBatch", t.VerifyBatch);
        TrackedInvoiceRegistry.Remove(SpecHash);
    }

    [Theory]
    [InlineData("/lnurl/verifyBatch")]
    [InlineData("ftp://h.example/lnurl/verifyBatch")]
    [InlineData("not a url")]
    public async Task CreateInvoice_ignores_a_verifyBatch_that_is_not_an_absolute_http_url(string verifyBatch)
    {
        var host = UniqueHost("vbbad");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb", 250_000_000))
            .Map($"https://{host}/cb?amount=250000000", SpecCallback(host, ",\"verifyBatch\":\"" + verifyBatch + "\""));

        await Receiver(host, http, Network.Main)
            .CreateInvoice(LightMoney.MilliSatoshis(250_000_000), "x", null, TestContext.Current.CancellationToken);

        Assert.True(TrackedInvoiceRegistry.TryGet(SpecHash, out var t));
        Assert.Null(t.VerifyBatch);
        TrackedInvoiceRegistry.Remove(SpecHash);
    }

    [Fact]
    public async Task CreateInvoice_does_not_downgrade_an_https_verify_to_an_http_verifyBatch()
    {
        var host = UniqueHost("vbdown");
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayJson($"https://{host}/cb", 250_000_000))
            .Map($"https://{host}/cb?amount=250000000", SpecCallback(host, $",\"verifyBatch\":\"http://{host}/lnurl/verifyBatch\""));

        await Receiver(host, http, Network.Main)
            .CreateInvoice(LightMoney.MilliSatoshis(250_000_000), "x", null, TestContext.Current.CancellationToken);

        Assert.True(TrackedInvoiceRegistry.TryGet(SpecHash, out var t));
        Assert.Null(t.VerifyBatch);
        TrackedInvoiceRegistry.Remove(SpecHash);
    }

    [Fact]
    public async Task A_verify_response_carrying_verifyBatch_moves_the_invoice_to_batch_polling()
    {
        var host = UniqueHost("retro");
        var hash = NewHash();
        TrackedInvoiceRegistry.Add(new TrackedInvoice(hash, SpecBolt11, $"https://{host}/verify/{hash}", host,
            $"https://{host}/pay", DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}",
            $"{{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"x\",\"verifyBatch\":\"https://{host}/lnurl/verifyBatch\"}}");

        var inv = await Receiver(host, http, Network.Main).GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.Equal(LightningInvoiceStatus.Unpaid, inv!.Status);
        Assert.True(TrackedInvoiceRegistry.TryGet(hash, out var t));
        Assert.Equal($"https://{host}/lnurl/verifyBatch", t.VerifyBatch);
        TrackedInvoiceRegistry.Remove(hash);
    }

    [Fact]
    public async Task GetInvoice_answers_from_the_pollers_last_result_without_a_request()
    {
        var host = UniqueHost("cache");
        var hash = NewHash();
        TrackedInvoiceRegistry.Add(new TrackedInvoice(hash, SpecBolt11, $"https://{host}/verify/{hash}", host,
            $"https://{host}/pay", DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp();
        var rx = Receiver(host, http, Network.Main);

        TrackedInvoiceRegistry.RecordResult(hash, null);
        Assert.Null(await rx.GetInvoice(hash, TestContext.Current.CancellationToken));
        var unpaid = new LightningInvoice { Id = hash, PaymentHash = hash, Status = LightningInvoiceStatus.Unpaid };
        TrackedInvoiceRegistry.RecordResult(hash, unpaid);
        Assert.Same(unpaid, await rx.GetInvoice(hash, TestContext.Current.CancellationToken));

        Assert.Empty(http.Requests);
        TrackedInvoiceRegistry.Remove(hash);
    }

    // A real payment on lnurl.mutinynet.arkade.sh, a signet: its invoice carries the lntbs prefix.
    [Fact]
    public async Task A_settled_signet_invoice_is_paid()
    {
        const string bolt11 = "lntbs578940n1p4tlqwspp5jd7c73dwwf9wh0ucq72n3pqwu8nasx7c45hg3uj9gasv6whkkkqqdqqcqzpkxqrpc8sp5mwwycj7ejsf205vk56p69nan0gmgh72da9pwm9x9pu68pzgytvss9qxpqysgqzrjznl05nyc8u0reg4uzvlhruac5y2cdmuxnakhf4hl3g3tkjwz5594rs3zw3gc4lyfdchd9pld2kwkvgcm8ptqp95meehfztry9n8qpu3kpz0";
        const string hash = "937d8f45ae724aebbf98079538840ee1e7d81bd8ad2e88f2454760cd3af6b580";
        const string preimage = "af96025cad3d29e62dd05073f4dc861053dca651e109c4f4d7329d917dd442f7";
        var host = UniqueHost("signet");
        var verify = $"https://{host}/lnurl/verify/{hash}";
        var http = new FakeHttp().Map(verify, $"{{\"status\":\"OK\",\"settled\":true,\"preimage\":\"{preimage}\"}}");
        var t = new TrackedInvoice(hash, bolt11, verify, host, $"https://{host}/pay", DateTimeOffset.UtcNow.AddHours(1));

        var paid = await LNURLReceiver.PollAndBuild(t, http.Client(), TestContext.Current.CancellationToken);

        Assert.Equal(LightningInvoiceStatus.Paid, paid!.Status);
        Assert.Equal(preimage, paid.Preimage);
        Assert.Equal(LightMoney.Satoshis(57894), paid.AmountReceived);
    }

    [Theory]
    [InlineData("lnbc1", "Mainnet")]
    [InlineData("lntb1", "Testnet")]
    [InlineData("lntbs1", "Signet")]
    [InlineData("lnbcrt1", "Regtest")]
    public void An_invoice_prefix_names_its_chain(string bolt11, string chain) =>
        Assert.Equal(chain, LNURLReceiver.InferNetwork(bolt11).ChainName.ToString());

    [Fact]
    public async Task CheckVerifySupport_flags_missing_verify()
    {
        var host = "nv.example";
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayMeta.Replace("{CB}", $"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", $"{{\"pr\":\"{RegtestInvoice(1000)}\"}}"); // invoice returned, but no verify field
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http.Client(), NullLogger.Instance);

        var err = await rx.CheckVerifySupport(TestContext.Current.CancellationToken);

        Assert.NotNull(err);
        Assert.Contains("verify", err);
    }

    [Fact]
    public async Task CheckVerifySupport_passes_when_verify_present()
    {
        var host = "yv.example";
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayMeta.Replace("{CB}", $"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", $"{{\"pr\":\"{RegtestInvoice(1000)}\",\"verify\":\"https://{host}/lnurlp/verify/abc\"}}");
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http.Client(), NullLogger.Instance);

        var err = await rx.CheckVerifySupport(TestContext.Current.CancellationToken);

        Assert.Null(err);
    }

    [Fact]
    public async Task GetInvoice_returns_paid_from_settled_cache_not_null()
    {
        // Simulate the poller having already settled + un-tracked this invoice. GetInvoice must still
        // report Paid (not null), or BTCPay's poll path (LightningListener.PollPayment) evicts it.
        var host = "settledrx.example";
        var hash = new string('d', 64);
        var paid = new LightningInvoice { Id = hash, PaymentHash = hash, Status = LightningInvoiceStatus.Paid };
        TrackedInvoiceRegistry.MarkSettled(hash, paid, DateTimeOffset.UtcNow.AddMinutes(5));

        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, new FakeHttp().Client(), NullLogger.Instance);

        var inv = await rx.GetInvoice(hash, TestContext.Current.CancellationToken);

        Assert.NotNull(inv);
        Assert.Equal(LightningInvoiceStatus.Paid, inv!.Status);
    }

    [Fact]
    public async Task CreateInvoice_rejects_amount_mismatch()
    {
        var host = "mm.example";
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayMeta.Replace("{CB}", $"https://{host}/cb"))
            // Request 100,000 msat but the callback returns the 250,000,000 msat spec bolt11 -> guard trips.
            .Map($"https://{host}/cb?amount=100000", $"{{\"pr\":\"{SpecBolt11}\",\"verify\":\"https://{host}/verify/x\"}}");
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.Main, http.Client(), NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            rx.CreateInvoice(LightMoney.MilliSatoshis(100_000), "x", null, TestContext.Current.CancellationToken));
        Assert.Contains("requested", ex.Message);
    }
}
