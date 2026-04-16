using System;
using System.Linq;
using System.Net.Http;
using BTCPayServer.HostedServices;
using NBXplorer;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces ExplorerClientProvider. Creates ExplorerClient instances backed by
/// ElectrumHttpHandler, so all NBXplorer HTTP calls are intercepted and routed
/// to our Electrum engine.
/// </summary>
public class ElectrumExplorerClientProvider : ExplorerClientProvider
{
    public ElectrumExplorerClientProvider(
        BTCPayNetworkProvider networkProvider,
        NBXplorerDashboard dashboard,
        ElectrumHttpHandler handler)
        : base(networkProvider, dashboard)
    {
        foreach (var network in networkProvider.GetAll().OfType<BTCPayNetwork>())
        {
            var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://electrum-shim.internal"),
                Timeout = TimeSpan.FromMinutes(5)
            };

            var explorerClient = network.NBXplorerNetwork.CreateExplorerClient(
                new Uri("http://electrum-shim.internal"));
            explorerClient.SetClient(httpClient);
            explorerClient.SetNoAuth();

            _Clients[network.CryptoCode.ToUpperInvariant()] = explorerClient;
        }
    }
}
