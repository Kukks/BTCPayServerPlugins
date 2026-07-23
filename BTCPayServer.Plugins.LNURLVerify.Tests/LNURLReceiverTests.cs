using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

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

    [Fact]
    public async Task CheckVerifySupport_flags_missing_verify()
    {
        var host = "nv.example";
        var http = new FakeHttp()
            .Map($"https://{host}/pay", PayMeta.Replace("{CB}", $"https://{host}/cb"))
            .Map($"https://{host}/cb?amount=1000", "{\"pr\":\"lnbc1\"}"); // invoice returned, but no verify field
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
            .Map($"https://{host}/cb?amount=1000", $"{{\"pr\":\"lnbc1\",\"verify\":\"https://{host}/lnurlp/verify/abc\"}}");
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
