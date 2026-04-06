using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Common;
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
    private readonly NBXplorerDashboard _dashboard;
    private readonly Dictionary<string, ExplorerClient> _clients = new();

    public ElectrumExplorerClientProvider(
        BTCPayNetworkProvider networkProvider,
        NBXplorerDashboard dashboard,
        ElectrumHttpHandler handler)
        : base(CreateEmptyHttpClientFactory(), networkProvider, CreateEmptyOptions(), dashboard, CreateEmptyLogs())
    {
        _dashboard = dashboard;

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

            _clients[network.CryptoCode.ToUpperInvariant()] = explorerClient;
        }
    }

    public new ExplorerClient GetExplorerClient(string cryptoCode)
    {
        if (cryptoCode == null) return null;
        _clients.TryGetValue(cryptoCode.ToUpperInvariant(), out var client);
        return client;
    }

    public new ExplorerClient GetExplorerClient(BTCPayNetworkBase network)
    {
        if (network == null) return null;
        return GetExplorerClient(network.CryptoCode);
    }

    public new bool IsAvailable(BTCPayNetworkBase network)
    {
        return IsAvailable(network.CryptoCode);
    }

    public new bool IsAvailable(string cryptoCode)
    {
        cryptoCode = cryptoCode.ToUpperInvariant();
        return _clients.ContainsKey(cryptoCode) && _dashboard.IsFullySynched(cryptoCode, out _);
    }

    public new IEnumerable<(BTCPayNetwork, ExplorerClient)> GetAll()
    {
        foreach (var kvp in _clients)
        {
            var network = NetworkProviders.GetNetwork<BTCPayNetwork>(kvp.Key);
            if (network != null)
                yield return (network, kvp.Value);
        }
    }

    private static IHttpClientFactory CreateEmptyHttpClientFactory()
    {
        return new NullHttpClientFactory();
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
