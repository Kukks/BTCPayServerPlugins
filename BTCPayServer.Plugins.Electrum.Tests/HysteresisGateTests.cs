using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class HysteresisGateTests
{
    [Theory]
    [InlineData(WalletBackend.Electrum, WalletBackend.Nbx, 4, 4, true,  true)]   // stable long enough, cooldown ok -> flip
    [InlineData(WalletBackend.Electrum, WalletBackend.Nbx, 3, 4, true,  false)]  // not stable enough
    [InlineData(WalletBackend.Electrum, WalletBackend.Nbx, 4, 4, false, false)]  // in cooldown
    [InlineData(WalletBackend.Nbx,      WalletBackend.Nbx, 9, 4, true,  false)]  // desired == current
    [InlineData(WalletBackend.Nbx,      WalletBackend.Electrum, 3, 3, true, true)] // failover ready
    public void ShouldFlip(WalletBackend cur, WalletBackend des, int agree, int req, bool cool, bool expected)
        => Assert.Equal(expected, HysteresisGate.ShouldFlip(cur, des, agree, req, cool));

    [Theory]
    [InlineData(WalletBackend.Nbx, 4)]
    [InlineData(WalletBackend.Electrum, 3)]
    public void RequiredFor(WalletBackend desired, int expected)
        => Assert.Equal(expected, HysteresisGate.RequiredFor(desired));
}
