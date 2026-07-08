using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class IndexFastForwarderTests
{
    [Theory]
    [InlineData(0, 5, true)]   // NBX at 0, high-water 5 -> must burn
    [InlineData(6, 5, false)]  // already past
    [InlineData(5, 5, true)]   // equal -> burn one more (5 already handed out)
    [InlineData(0, -1, false)] // nothing reserved
    public void NeedsBurn(int backendNext, int highWater, bool expected)
        => Assert.Equal(expected, IndexFastForwarder.NeedsBurn(backendNext, highWater));
}
