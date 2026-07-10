using System.Net.Http;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class ConnectionStringTypeGateTests
{
    static LNURLVerifyConnectionStringHandler NewHandler() =>
        new(new SimpleHttpClientFactory(), NullLoggerFactory.Instance);

    [Fact]
    public void Ignores_non_lnurl_types()
    {
        var h = NewHandler();
        var client = h.Create("type=lnd;server=https://x", Network.RegTest, out var error);
        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void Rejects_missing_value()
    {
        var h = NewHandler();
        var client = h.Create("type=lnurl;", Network.RegTest, out var error);
        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Contains("value", error);
    }
}

sealed class SimpleHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
