using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using LNURL;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.Backend.Controllers;

namespace BTCPayServer.Plugins.Wabisabi;

public class Nostr
{
    public static int Kind = 15750;
    public static string TypeTagIdentifier = "type";
    public static string TypeTagValue = "wabisabi";
    public static string NetworkTagIdentifier = "network";
    public static string EndpointTagIdentifier = "endpoint";

    public static async Task Publish(
        Uri relayUri,
        NostrEvent[] evts,
        Socks5HttpClientHandler? httpClientHandler,
        CancellationToken cancellationToken )
    {
        
        if (!evts.Any())
            return;
        var ct = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token)
            .Token;
        var client = new NostrClient(relayUri, socket =>
        {
            if (socket is ClientWebSocket clientWebSocket && httpClientHandler != null)
            {
                clientWebSocket.Options.Proxy = httpClientHandler.Proxy;
            }
        });
        await  client.Connect(ct);
       
        await client.SendEventsAndWaitUntilReceived(evts, ct);
        client.Dispose();
    }

    public static async Task<NostrEvent> CreateCoordinatorDiscoveryEvent(Network currentNetwork,
        ECPrivKey key,
        Uri coordinatorUri,
        string description)
    {
        var evt = new NostrEvent()
        {
            Kind = Kind,
            Content =  description??string.Empty,
            Tags = new List<NostrEventTag>()
            {
                new() {TagIdentifier = EndpointTagIdentifier, Data = new List<string>() {coordinatorUri.ToString()}},
                new() {TagIdentifier = TypeTagIdentifier, Data = new List<string>() { TypeTagValue}},
                new() {TagIdentifier = NetworkTagIdentifier, Data = new List<string>() {currentNetwork.ChainName.ToString().ToLower()}}
            }
        };
        
        await evt.ComputeIdAndSignAsync(key);
        return evt;
    }

    public static async Task<List<DiscoveredCoordinator>> Discover(
        Socks5HttpClientHandler? httpClientHandler,
        Uri relayUri,
        Network currentNetwork,
        string ourPubKey,
        CancellationToken cancellationToken)
    {
        
        var nostrClient = new NostrClient(relayUri, socket =>
        {
            if (socket is ClientWebSocket clientWebSocket && httpClientHandler != null)
            {
                clientWebSocket.Options.Proxy = httpClientHandler.Proxy;
            }
        });
        var result = new List<NostrEvent>();
        
        var cts = CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token,
            cancellationToken);
        await nostrClient.Connect(cts.Token);

        
        result = await nostrClient.SubscribeForEvents(
            new[]
            {
                new NostrSubscriptionFilter()
                {
                    Kinds = new[] {Kind},
                    ExtensionData = new Dictionary<string, JsonElement>()
                    {
                        // ["#type"] = JsonSerializer.SerializeToElement(new[] {TypeTagValue}),
                        // ["#network"] = JsonSerializer.SerializeToElement(new[] {network, currentNetwork.Name.ToLower()})
                    },
                    Limit = 1000
                }
            }, true, cts.Token).ToListAsync(cancellationToken);

        nostrClient.Dispose();
var network = new []{currentNetwork.ChainName.ToString().ToLower(), currentNetwork.Name.ToLower()};
        return result.Where(@event =>
            @event.PublicKey != ourPubKey && 
            @event.Verify() &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == EndpointTagIdentifier &&
                tag.Data.Any(s => Uri.IsWellFormedUriString(s, UriKind.Absolute))) &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier.Equals(TypeTagIdentifier, StringComparison.InvariantCultureIgnoreCase) &&
                tag.Data.Any(tag => tag.Equals(TypeTagValue, StringComparison.InvariantCultureIgnoreCase)) &&
            @event.Tags.Any(tag => tag.TagIdentifier.Equals(NetworkTagIdentifier, StringComparison.InvariantCultureIgnoreCase) && 
                                   tag.Data.Any(s => network.Contains(s, StringComparer.InvariantCultureIgnoreCase))))
        ).OrderByDescending(@event => @event.CreatedAt)
            .DistinctBy(@event => @event.PublicKey)
            .Select(@event => new DiscoveredCoordinator()
        {
            Description = @event.Content,
            Name = @event.PublicKey,
            Uri = new Uri(@event.GetTaggedData("endpoint")
                .First(s => Uri.IsWellFormedUriString(s, UriKind.Absolute)))
        }).ToList();
    }
}
