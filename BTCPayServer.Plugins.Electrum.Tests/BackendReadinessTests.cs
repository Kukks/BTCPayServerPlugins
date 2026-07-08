using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class BackendReadinessTests
{
    [Theory]
    [InlineData(true,  true,  WalletBackend.Nbx)]
    [InlineData(false, true,  WalletBackend.Electrum)]
    [InlineData(true,  false, WalletBackend.Electrum)]
    [InlineData(false, false, WalletBackend.Electrum)]
    public void Decision(bool nbxSynced, bool trackedInNbx, WalletBackend expected)
        => Assert.Equal(expected, BackendCoordinator.DecideBackend(nbxSynced, trackedInNbx));
}
