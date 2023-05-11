using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using LNURL;
using NBitcoin;
using Newtonsoft.Json.Linq;
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
        var client = new NostrClient(relayUri, socket => socket.Options.Proxy = httpClientHandler?.Proxy);
        _ = client.Connect(ct);
        await client.WaitUntilConnected(ct);
       
        await client.SendEventsAndWaitUntilReceived(evts, ct);
        client.Dispose();
    }

    public static async Task<NostrEvent> CreateCoordinatorDiscoveryEvent(Network currentNetwork,
        string key,
        Uri coordinatorUri,
        string description)
    {
        var privateKey = NostrExtensions.ParseKey(key);
        var evt = new NostrEvent()
        {
            Kind = Kind,
            Content =  description,
            Tags = new List<NostrEventTag>()
            {
                new() {TagIdentifier = EndpointTagIdentifier, Data = new List<string>() {new Uri(coordinatorUri, "plugins/wabisabi-coordinator").ToString()}},
                new() {TagIdentifier = TypeTagIdentifier, Data = new List<string>() { TypeTagValue}},
                new() {TagIdentifier = NetworkTagIdentifier, Data = new List<string>() {currentNetwork.Name.ToLower()}}
            }
        };
        
        await evt.ComputeIdAndSignAsync(privateKey);
        return evt;
    }

    public static async Task<List<DiscoveredCoordinator>> Discover(
        Socks5HttpClientHandler? httpClientHandler,
        Uri relayUri,
        Network currentNetwork,
        string ourPubKey,
        CancellationToken cancellationToken)
    {
        
        var nostrClient = new NostrClient(relayUri, socket => socket.Options.Proxy = relayUri.IsLocalNetwork()? null: httpClientHandler?.Proxy);
        var result = new List<NostrEvent>();
        var network = currentNetwork.Name.ToLower();
        
        var cts = CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token,
            cancellationToken);
        _ = nostrClient.Connect(cts.Token);
        await nostrClient.WaitUntilConnected(cts.Token);

        result = await nostrClient.SubscribeForEvents(
            new[]
            {
                new NostrSubscriptionFilter()
                {
                    Kinds = new[] {Kind},
                    Since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)),
                    ExtensionData = new Dictionary<string, JsonElement>()
                    {
                        ["#type"] = JsonSerializer.SerializeToElement(new[] {TypeTagValue}),
                        ["#network"] = JsonSerializer.SerializeToElement(new[] {network})
                    }
                }
            }, true, cts.Token).ToListAsync();

        nostrClient.Dispose();

        return result.Where(@event =>
            @event.PublicKey != ourPubKey &&
            @event.CreatedAt >= DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)) && 
            @event.Verify() &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == EndpointTagIdentifier &&
                tag.Data.Any(s => Uri.IsWellFormedUriString(s, UriKind.Absolute))) &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == TypeTagIdentifier &&
                tag.Data.FirstOrDefault() == TypeTagValue) &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == NetworkTagIdentifier && tag.Data.FirstOrDefault()?.Equals(network, StringComparison.InvariantCultureIgnoreCase) is true)
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
