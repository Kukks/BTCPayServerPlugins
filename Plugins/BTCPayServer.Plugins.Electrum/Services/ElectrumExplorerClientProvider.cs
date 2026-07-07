using System;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Options;
using NBXplorer;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces ExplorerClientProvider. Creates ExplorerClient instances whose REST
/// transport is ElectrumHttpHandler (so HTTP calls are intercepted and routed to
/// our Electrum engine), but whose URI + cookie auth point at real NBXplorer.
/// NBXplorerListener opens a websocket that bypasses the HTTP handler and talks
/// directly to the client's URI, so pointing that URI at real NBX keeps the
/// listener (and its cookie-authenticated connection) working unmodified.
/// </summary>
public class ElectrumExplorerClientProvider : ExplorerClientProvider
{
    public ElectrumExplorerClientProvider(
        BTCPayNetworkProvider networkProvider,
        NBXplorerDashboard dashboard,
        ElectrumHttpHandler handler,
        RealNbxGateway realNbxGateway,
        IOptions<NBXplorerOptions> nbxOptions)
        : base(networkProvider, dashboard)
    {
        foreach (var network in networkProvider.GetAll().OfType<BTCPayNetwork>())
        {
            var real = realNbxGateway.GetClient(network.CryptoCode);
            var uri = real?.Address ?? new Uri("http://electrum-shim.internal");

            var httpClient = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            var explorerClient = network.NBXplorerNetwork.CreateExplorerClient(uri);
            explorerClient.SetClient(httpClient);

            // Cookie auth is not exposed on RealNbxGateway's client, so read it from
            // the same NBXplorerOptions source RealNbxGateway itself uses.
            var setting = nbxOptions.Value.NBXplorerConnectionSettings
                .FirstOrDefault(s => string.Equals(s.CryptoCode, network.CryptoCode, StringComparison.OrdinalIgnoreCase));
            var cookieFile = setting?.CookieFile?.Trim();
            if (string.IsNullOrEmpty(cookieFile) || cookieFile == "0")
                explorerClient.SetNoAuth();
            else
                explorerClient.SetCookieAuth(cookieFile);

            _Clients[network.CryptoCode.ToUpperInvariant()] = explorerClient;
        }
    }
}
