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
    static LNURL.LNURLWithdrawRequest Withdraw(long minMsat, long maxMsat) => new()
    {
        Callback = new Uri("https://h.example/w"),
        K1 = "k1",
        MinWithdrawable = LightMoney.MilliSatoshis(minMsat),
        MaxWithdrawable = LightMoney.MilliSatoshis(maxMsat)
    };

    static ResolvedLnurl Resolved(LNURL.LNURLWithdrawRequest w) =>
        new(LnurlCapability.SendAndReceive, new Uri("https://h.example/pay"), w, "h.example");

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
}
