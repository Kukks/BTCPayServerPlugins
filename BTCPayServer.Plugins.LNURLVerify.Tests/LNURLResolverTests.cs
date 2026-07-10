using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.LNURLVerify;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LNURLResolverTests
{
    const string Pay =
        "{\"tag\":\"payRequest\",\"callback\":\"https://h.example/cb\",\"minSendable\":1000,\"maxSendable\":100000000,\"metadata\":\"[[\\\"text/plain\\\",\\\"x\\\"]]\"}";

    [Fact]
    public async Task LnAddress_is_receive_only()
    {
        var http = new FakeHttp().Map("https://ln.example/.well-known/lnurlp/alice", Pay);
        var r = await LNURLResolver.Resolve("alice@ln.example", Network.RegTest, http.Client(), CancellationToken.None);
        Assert.Equal(LnurlCapability.ReceiveOnly, r.Capability);
        Assert.Null(r.Withdraw);
    }

    [Fact]
    public async Task Withdraw_with_payLink_is_send_and_receive()
    {
        var withdraw =
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"abc\",\"minWithdrawable\":1000,\"maxWithdrawable\":100000000,\"payLink\":\"https://h.example/pay\"}";
        var http = new FakeHttp()
            .Map("https://h.example/withdraw", withdraw)
            .Map("https://h.example/pay", Pay);
        var r = await LNURLResolver.Resolve("https://h.example/withdraw", Network.RegTest, http.Client(), CancellationToken.None);
        Assert.Equal(LnurlCapability.SendAndReceive, r.Capability);
        Assert.NotNull(r.Withdraw);
        Assert.Equal("https://h.example/pay", r.PayEndpoint.ToString());
    }

    [Fact]
    public async Task Withdraw_without_payLink_errors()
    {
        var withdraw =
            "{\"tag\":\"withdrawRequest\",\"callback\":\"https://h.example/w\",\"k1\":\"abc\",\"minWithdrawable\":1000,\"maxWithdrawable\":100000000}";
        var http = new FakeHttp().Map("https://h.example/withdraw", withdraw);
        await Assert.ThrowsAsync<System.FormatException>(() =>
            LNURLResolver.Resolve("https://h.example/withdraw", Network.RegTest, http.Client(), CancellationToken.None));
    }
}
