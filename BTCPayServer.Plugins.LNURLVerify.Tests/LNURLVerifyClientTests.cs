using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LNURLVerifyClientTests
{
    static LNURLVerifyLightningClient ReceiveOnly()
    {
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri("https://h.example/pay"), null, null, "h.example");
        return new LNURLVerifyLightningClient(resolved, Network.RegTest, new FakeHttp().Client(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ReceiveOnly_Pay_throws_NotSupported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => ReceiveOnly().Pay("lnbcrt1", CancellationToken.None));

    [Fact]
    public async Task ReceiveOnly_GetBalance_throws_NotSupported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => ReceiveOnly().GetBalance(CancellationToken.None));

    [Fact]
    public void DisplayName_and_ServerUri_present()
    {
        var c = ReceiveOnly();
        Assert.Equal("LNURL", c.DisplayName);
        Assert.Equal("https://h.example/", c.ServerUri!.ToString());
    }

    [Fact]
    public async Task ReceiveOnly_ListPayments_is_empty() =>
        Assert.Empty(await ReceiveOnly().ListPayments(CancellationToken.None));

    [Fact]
    public async Task GetPayment_returns_a_recorded_send()
    {
        var hash = new string('e', 64);
        SentPaymentRegistry.Record(new LightningPayment
        { Id = hash, PaymentHash = hash, Status = LightningPaymentStatus.Complete });

        var got = await ReceiveOnly().GetPayment(hash, CancellationToken.None);

        Assert.NotNull(got);
        Assert.Equal(LightningPaymentStatus.Complete, got!.Status);
    }
}
