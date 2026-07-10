using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LNURLSendExecutorTests
{
    // A canonical BOLT#11 spec example (mainnet, 2500 micro-BTC = 250,000,000 msat). Parses offline;
    // used only to drive the Pay chain in unit tests — it need not be payable.
    const string SpecBolt11 =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    [Fact]
    public void SpecBolt11_parses_offline_with_expected_amount()
    {
        var b = BOLT11PaymentRequest.Parse(SpecBolt11, NBitcoin.Network.Main);
        Assert.Equal(LightMoney.MilliSatoshis(250_000_000), b.MinimumAmount);
    }

    static LNURL.LNURLWithdrawRequest Withdraw(long minMsat, long maxMsat) => new()
    {
        Callback = new Uri("https://h.example/w"),
        K1 = "k1",
        MinWithdrawable = LightMoney.MilliSatoshis(minMsat),
        MaxWithdrawable = LightMoney.MilliSatoshis(maxMsat)
    };

    static ResolvedLnurl Resolved(LNURL.LNURLWithdrawRequest w) =>
        new(LnurlCapability.SendAndReceive, new Uri("https://h.example/pay"), w, new Uri("https://h.example/withdraw"), "h.example");

    [Fact]
    public void WithinBounds_respects_min_and_max()
    {
        var w = Withdraw(1000, 5000);
        Assert.True(LNURLSendExecutor.WithinBounds(LightMoney.MilliSatoshis(3000), w));
        Assert.False(LNURLSendExecutor.WithinBounds(LightMoney.MilliSatoshis(500), w));
        Assert.False(LNURLSendExecutor.WithinBounds(LightMoney.MilliSatoshis(9000), w));
    }

    [Fact]
    public async Task Pay_invalid_bolt11_returns_error_not_throw()
    {
        var http = new FakeHttp();
        var exec = new LNURLSendExecutor(Resolved(Withdraw(1000, 5000)), http.Client(), NullLogger.Instance);
        var resp = await exec.Pay("not-a-bolt11", null, CancellationToken.None);
        Assert.Equal(PayResult.Error, resp.Result);
    }

    [Fact]
    public async Task GetBalance_returns_current_balance_when_present()
    {
        var w = Withdraw(1000, 5000);
        w.CurrentBalance = LightMoney.MilliSatoshis(4200);
        var exec = new LNURLSendExecutor(Resolved(w), new FakeHttp().Client(), NullLogger.Instance);
        var bal = await exec.GetBalance(CancellationToken.None);
        Assert.Equal(LightMoney.MilliSatoshis(4200), bal);
    }

    [Fact]
    public async Task RefreshWithdraw_returns_fresh_k1_via_balanceCheck()
    {
        var w = Withdraw(1000, 5000);
        w.K1 = "stale";
        w.BalanceCheck = new Uri("https://h.example/bc");
        var http = new FakeHttp().Map("https://h.example/bc",
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"fresh\",\"minWithdrawable\":1000,\"maxWithdrawable\":5000}");
        var exec = new LNURLSendExecutor(Resolved(w), http.Client(), NullLogger.Instance);

        var fresh = await exec.RefreshWithdraw(CancellationToken.None);

        Assert.Equal("fresh", fresh.K1);
    }

    [Fact]
    public async Task RefreshWithdraw_falls_back_to_withdraw_endpoint_when_no_balanceCheck()
    {
        var w = Withdraw(1000, 5000);
        w.K1 = "stale"; // no BalanceCheck set -> re-hit the original withdraw endpoint
        var http = new FakeHttp().Map("https://h.example/withdraw",
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"fresh2\",\"minWithdrawable\":1000,\"maxWithdrawable\":5000}");
        var exec = new LNURLSendExecutor(Resolved(w), http.Client(), NullLogger.Instance);

        var fresh = await exec.RefreshWithdraw(CancellationToken.None);

        Assert.Equal("fresh2", fresh.K1);
    }

    [Fact]
    public async Task Pay_refreshes_k1_then_submits_with_fresh_k1_and_returns_ok()
    {
        var w = Withdraw(1000, 300_000_000); // covers the 250,000,000 msat bolt11
        w.K1 = "stale";
        w.BalanceCheck = new Uri("https://h.example/bc");
        var http = new FakeHttp()
            .Map("https://h.example/bc",
                "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"fresh\",\"minWithdrawable\":1000,\"maxWithdrawable\":300000000}")
            .Map($"https://h.example/w?k1=fresh&pr={SpecBolt11}", "{\"status\":\"OK\"}");
        var exec = new LNURLSendExecutor(Resolved(w), http.Client(), NullLogger.Instance);

        var resp = await exec.Pay(SpecBolt11, null, CancellationToken.None);

        Assert.Equal(PayResult.Ok, resp.Result);
        // Submitted with the FRESH k1 (from balanceCheck), never the stale resolve-time one.
        Assert.Contains(http.Requests, r => r.Contains("k1=fresh") && r.Contains("pr="));
        Assert.DoesNotContain(http.Requests, r => r.Contains("k1=stale"));
    }

    [Fact]
    public async Task Pay_rejects_amount_over_refreshed_max()
    {
        var w = Withdraw(1000, 300_000_000);
        w.BalanceCheck = new Uri("https://h.example/bc");
        // The refreshed withdraw tightens max BELOW the bolt11 amount -> gate on the refreshed bounds.
        var http = new FakeHttp().Map("https://h.example/bc",
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"fresh\",\"minWithdrawable\":1000,\"maxWithdrawable\":1000}");
        var exec = new LNURLSendExecutor(Resolved(w), http.Client(), NullLogger.Instance);

        var resp = await exec.Pay(SpecBolt11, null, CancellationToken.None);

        Assert.Equal(PayResult.Error, resp.Result);
    }

    [Fact]
    public async Task Pay_records_completed_send_for_reconciliation()
    {
        var w = Withdraw(1000, 300_000_000);
        w.BalanceCheck = new Uri("https://h.example/bc");
        var http = new FakeHttp()
            .Map("https://h.example/bc",
                "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"fresh\",\"minWithdrawable\":1000,\"maxWithdrawable\":300000000}")
            .Map($"https://h.example/w?k1=fresh&pr={SpecBolt11}", "{\"status\":\"OK\"}");
        var exec = new LNURLSendExecutor(Resolved(w), http.Client(), NullLogger.Instance);

        await exec.Pay(SpecBolt11, null, CancellationToken.None);

        // Scoped to the connection the executor sends on (Resolved(w).PayEndpoint).
        var conn = "https://h.example/pay";
        var hash = BOLT11PaymentRequest.Parse(SpecBolt11, NBitcoin.Network.Main).PaymentHash!.ToString();
        Assert.True(SentPaymentRegistry.TryGet(conn, hash, out var p));
        Assert.Equal(LightningPaymentStatus.Complete, p.Status);
        Assert.Equal(hash, p.PaymentHash);
    }

    [Fact]
    public void SentPaymentRegistry_prune_drops_old_terminal_but_keeps_recent_and_pending()
    {
        const string conn = "https://prune.example/pay";
        SentPaymentRegistry.Record(conn, new LightningPayment
        { Id = "prune_old", PaymentHash = "prune_old", Status = LightningPaymentStatus.Complete, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        SentPaymentRegistry.Record(conn, new LightningPayment
        { Id = "prune_new", PaymentHash = "prune_new", Status = LightningPaymentStatus.Complete, CreatedAt = DateTimeOffset.UtcNow });
        // An old but Pending (uncertain) send must NOT be pruned (else BTCPay would auto-cancel + re-pay).
        SentPaymentRegistry.Record(conn, new LightningPayment
        { Id = "prune_pending", PaymentHash = "prune_pending", Status = LightningPaymentStatus.Pending, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });

        SentPaymentRegistry.Prune(DateTimeOffset.UtcNow.AddHours(-24));

        Assert.False(SentPaymentRegistry.TryGet(conn, "prune_old", out _));
        Assert.True(SentPaymentRegistry.TryGet(conn, "prune_new", out _));
        Assert.True(SentPaymentRegistry.TryGet(conn, "prune_pending", out _));
    }

    [Fact]
    public void Link_locks_are_shared_per_connection_across_instances()
    {
        // BTCPay creates separate client instances per connection; the send lock must be shared across
        // them (keyed by connection) or concurrent Pays would race the k1-refresh+submit.
        var a1 = LNURLSendExecutor.GetLinkLock("https://x.example/pay");
        var a2 = LNURLSendExecutor.GetLinkLock("https://x.example/pay");
        var b = LNURLSendExecutor.GetLinkLock("https://y.example/pay");

        Assert.Same(a1, a2);   // same connection -> same lock
        Assert.NotSame(a1, b); // different connection -> different lock
    }
}
