#nullable enable
using System;
using System.Linq;
using System.Threading;
using BTCPayServer.Lightning;
using NBitcoin;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.NIP05;

public class NostrWalletConnectLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly NostrClientPool _nostrClientPool;

    public NostrWalletConnectLightningConnectionStringHandler(NostrClientPool nostrClientPool)
    {
        _nostrClientPool = nostrClientPool;
    }
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        
        
        if (!connectionString.StartsWith(NIP47.UriScheme, StringComparison.OrdinalIgnoreCase) && !connectionString.StartsWith("type=nwc;key="))
        {
            error = null;
            return null;
        }

        connectionString = connectionString.Replace("type=nwc;key=", "");
        if (!connectionString.StartsWith(NIP47.UriScheme, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Invalid nostr wallet connect uri (must start with {NIP47.UriScheme})";
            return null;
        }

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            error = "Invalid nostr wallet connect uri";
            return null;
        }

        error = null;
		return new NostrWalletConnectLightningClient(_nostrClientPool, uri, network);
	}
}