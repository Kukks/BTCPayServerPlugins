using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class BackendCoordinatorTests
{
    [Fact]
    public void Unknown_wallet_defaults_to_electrum()
    {
        var c = new BackendCoordinator();
        Assert.Equal(WalletBackend.Electrum, c.GetActiveBackend("scheme-1"));
    }

    [Fact]
    public void Set_then_get_roundtrips()
    {
        var c = new BackendCoordinator();
        c.SetActiveBackend("scheme-1", WalletBackend.Nbx);
        Assert.Equal(WalletBackend.Nbx, c.GetActiveBackend("scheme-1"));
        Assert.Equal(WalletBackend.Electrum, c.GetActiveBackend("scheme-2"));
    }
}
