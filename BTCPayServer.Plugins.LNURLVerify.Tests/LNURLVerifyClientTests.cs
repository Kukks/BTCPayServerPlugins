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
        await Assert.ThrowsAsync<NotSupportedException>(() => ReceiveOnly().Pay("lnbcrt1", TestContext.Current.CancellationToken));

    [Fact]
    public async Task ReceiveOnly_GetBalance_throws_NotSupported() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => ReceiveOnly().GetBalance(TestContext.Current.CancellationToken));

    [Fact]
    public void DisplayName_and_ServerUri_present()
    {
        var c = ReceiveOnly();
        Assert.Equal("LNURL", c.DisplayName);
        Assert.Equal("https://h.example/", c.ServerUri!.ToString());
    }

    [Fact]
    public async Task ReceiveOnly_ListPayments_is_empty() =>
        Assert.Empty(await ReceiveOnly().ListPayments(TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetPayment_returns_a_recorded_send_for_this_connection()
    {
        var hash = new string('e', 64);
        // Record under this client's connection key (ReceiveOnly().PayEndpoint) so the scoped lookup finds it.
        SentPaymentRegistry.Record("https://h.example/pay", new LightningPayment
        { Id = hash, PaymentHash = hash, Status = LightningPaymentStatus.Complete });

        var got = await ReceiveOnly().GetPayment(hash, TestContext.Current.CancellationToken);

        Assert.NotNull(got);
        Assert.Equal(LightningPaymentStatus.Complete, got!.Status);
    }

    [Fact]
    public async Task GetPayment_does_not_leak_another_connections_send()
    {
        var hash = new string('f', 64);
        SentPaymentRegistry.Record("https://other-store.example/pay", new LightningPayment
        { Id = hash, PaymentHash = hash, Status = LightningPaymentStatus.Complete });

        // This client (connection https://h.example/pay) must NOT see another connection's send.
        Assert.Null(await ReceiveOnly().GetPayment(hash, TestContext.Current.CancellationToken));
    }
}
