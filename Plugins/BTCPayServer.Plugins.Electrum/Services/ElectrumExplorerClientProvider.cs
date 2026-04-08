using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using BTCPayServer.HostedServices;
using NBXplorer;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces ExplorerClientProvider. Creates ExplorerClient instances backed by
/// ElectrumHttpHandler, so all NBXplorer HTTP calls are intercepted and routed
/// to our Electrum engine.
///
/// Since ExplorerClientProvider's methods (GetExplorerClient, IsAvailable, GetAll)
/// are NOT virtual, we can't override them. Instead, we populate the base class's
/// private _Clients dictionary via reflection so the base methods work correctly.
/// </summary>
public class ElectrumExplorerClientProvider : ExplorerClientProvider
{
    public ElectrumExplorerClientProvider(
        BTCPayNetworkProvider networkProvider,
        NBXplorerDashboard dashboard,
        ElectrumHttpHandler handler)
        : base(new NullHttpClientFactory(), networkProvider, CreateEmptyOptions(), dashboard, CreateEmptyLogs())
    {
        // Access the base class's private _Clients dictionary
        var clientsField = typeof(ExplorerClientProvider)
            .GetField("_Clients", BindingFlags.NonPublic | BindingFlags.Instance);
        var clients = (Dictionary<string, ExplorerClient>)clientsField!.GetValue(this);

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

            clients[network.CryptoCode.ToUpperInvariant()] = explorerClient;
        }
    }

    private static Microsoft.Extensions.Options.IOptions<BTCPayServer.Configuration.NBXplorerOptions> CreateEmptyOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(new BTCPayServer.Configuration.NBXplorerOptions());
    }

    private static BTCPayServer.Logging.Logs CreateEmptyLogs()
    {
        return new BTCPayServer.Logging.Logs();
    }

    private class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }
}
