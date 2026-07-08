using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class EventGateTests
{
    [Theory]
    [InlineData(WalletBackend.Electrum, true)]
    [InlineData(WalletBackend.Nbx, false)]
    public void ShouldElectrumPublish(WalletBackend active, bool expected)
        => Assert.Equal(expected, EventGate.ShouldElectrumPublish(active));
}
