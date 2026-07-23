using System;
using System.Net.Http;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class ConnectionStringBuildTests
{
    [Fact]
    public void Bad_value_yields_error_not_throw()
    {
        var h = new LNURLVerifyConnectionStringHandler(new SimpleHttpClientFactory(), NullLoggerFactory.Instance);
        var client = h.Create("type=lnurl;value=not-a-real-lnurl", Network.RegTest, out var error);
        Assert.Null(client);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Resolution_is_cached_across_Create_calls()
    {
        // Unique host so the process-wide static resolution cache isn't pre-populated by another test.
        var host = "cache" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".example";
        var pay = "{\"tag\":\"payRequest\",\"callback\":\"https://" + host + "/cb\",\"minSendable\":1000,\"maxSendable\":100000000,\"metadata\":\"[[\\\"text/plain\\\",\\\"x\\\"]]\"}";
        var fake = new FakeHttp().Map($"https://{host}/pay", pay);
        var h = new LNURLVerifyConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var c1 = h.Create($"type=lnurl;value=https://{host}/pay", Network.RegTest, out var e1);
        var c2 = h.Create($"type=lnurl;value=https://{host}/pay", Network.RegTest, out var e2);

        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        // First Create resolved over the network; the second hit the cache (no second fetch).
        Assert.Single(fake.Requests);
    }
}

sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly FakeHttp _fake;
    public FakeHttpClientFactory(FakeHttp fake) => _fake = fake;
    public HttpClient CreateClient(string name) => _fake.Client();
}
