using System;
using System.Threading;
using System.Threading.Tasks;
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
}
