using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.Electrum.Services;

public class ElectrumSyncSummaryProvider : ISyncSummaryProvider
{
    private readonly NBXplorerDashboard _dashboard;
    private readonly BTCPayNetworkProvider _networkProvider;

    public ElectrumSyncSummaryProvider(
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider)
    {
        _dashboard = dashboard;
        _networkProvider = networkProvider;
    }

    public bool AllAvailable()
    {
        return _dashboard.IsFullySynched();
    }

    public string Partial => "Electrum/ElectrumSyncSummary";

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        foreach (var network in _networkProvider.GetAll().OfType<BTCPayNetwork>())
        {
            var summary = _dashboard.Get(network.CryptoCode);
            var available = summary?.State == NBXplorerState.Ready;
            yield return new ElectrumSyncStatus(available)
            {
                PaymentMethodId = network.CryptoCode + "-CHAIN",
                ChainHeight = summary?.Status?.ChainHeight ?? 0,
                SyncHeight = summary?.Status?.SyncHeight ?? 0
            };
        }
    }
}

public class ElectrumSyncStatus : ISyncStatus
{
    public string PaymentMethodId { get; set; }
    public bool Available { get; }
    public int ChainHeight { get; set; }
    public int SyncHeight { get; set; }

    public ElectrumSyncStatus(bool available)
    {
        Available = available;
    }
}
