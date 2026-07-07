using System.Collections.Generic;
using System.Net.Http;
using BTCPayServer.Configuration;
using Microsoft.Extensions.Options;
using NBXplorer;

namespace BTCPayServer.Plugins.Electrum.Services;

// A direct real-NBX ExplorerClient (NOT routed through our handler), used by the
// coordinator (status/track/rescan/index) and by the routing handler's proxy path.
public class RealNbxGateway
{
    private readonly Dictionary<string, ExplorerClient> _clients = new();

    public RealNbxGateway(
        IHttpClientFactory httpClientFactory,
        BTCPayNetworkProvider networkProvider,
        IOptions<NBXplorerOptions> nbxOptions)
    {
        foreach (var setting in nbxOptions.Value.NBXplorerConnectionSettings)
        {
            if (setting.ExplorerUri == null) continue;
            var network = networkProvider.GetNetwork<BTCPayNetwork>(setting.CryptoCode);
            if (network == null) continue;

            var client = network.NBXplorerNetwork.CreateExplorerClient(setting.ExplorerUri);
            client.SetClient(httpClientFactory.CreateClient(nameof(RealNbxGateway)));
            var cookie = setting.CookieFile?.Trim();
            if (string.IsNullOrEmpty(cookie) || cookie == "0")
                client.SetNoAuth();
            else
                client.SetCookieAuth(cookie);

            _clients[setting.CryptoCode.ToUpperInvariant()] = client;
        }
    }

    public ExplorerClient? GetClient(string cryptoCode) =>
        _clients.TryGetValue(cryptoCode.ToUpperInvariant(), out var c) ? c : null;
}
