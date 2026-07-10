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
}
