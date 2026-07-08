namespace BTCPayServer.Plugins.Electrum.Services;

public static class EventGate
{
    // ElectrumListener only publishes for wallets it is the active backend for;
    // NBX-active wallets are handled by the (kept) core NBXplorerListener, so
    // publishing here too would double-fire NewOnChainTransactionEvent.
    public static bool ShouldElectrumPublish(WalletBackend active) => active == WalletBackend.Electrum;
}
