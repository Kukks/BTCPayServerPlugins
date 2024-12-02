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
        try
        {
            var connectParams = NIP47.ParseUri(uri); 
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (client, disposable) = _nostrClientPool.GetClientAndConnect(connectParams.relays,  cts.Token).ConfigureAwait(false).GetAwaiter().GetResult();
            using (disposable)
            {
                var commands = client.FetchNIP47AvailableCommands(connectParams.Item1, cancellationToken: cts.Token)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                var requiredCommands = new[] {"get_info", "make_invoice", "lookup_invoice", "list_transactions"};
                if (commands?.Commands is null || requiredCommands.Any(c => !commands.Value.Commands.Contains(c)))
                {
                    error =
                        "No commands available or not all required commands are available (get_info, make_invoice, lookup_invoice, list_transactions)";
                    return null;
                }

                var response = client
                    .SendNIP47Request<NIP47.GetInfoResponse>(connectParams.pubkey, connectParams.secret,
                        new NIP47.GetInfoRequest(), cancellationToken: cts.Token).ConfigureAwait(false).GetAwaiter()
                    .GetResult();

                var walletNetwork = response.Network;
                if (!network.ChainName.ToString().Equals(walletNetwork,
                        StringComparison.InvariantCultureIgnoreCase))
                {
                    error =
                        $"The network of the wallet ({walletNetwork}) does not match the network of the server ({network.ChainName})";
                    return null;
                }
                if (response?.Methods is null || requiredCommands.Any(c => !response.Methods.Contains(c)))
                {
                    error =
                        "No commands available or not all required commands are available (get_info, make_invoice, lookup_invoice, list_transactions)";
                    return null;
                }

                (string[] Commands, string[] Notifications) values = (response.Methods ?? commands.Value.Commands,
                    response.Notifications ?? commands.Value.Notifications);

                error = null;
                return new NostrWalletConnectLightningClient(_nostrClientPool, uri, network, values);
            }
        }
        catch (Exception e)
        {
            error = "Invalid nostr wallet connect uri: " + e.Message;
            return null;
        }
    }
}