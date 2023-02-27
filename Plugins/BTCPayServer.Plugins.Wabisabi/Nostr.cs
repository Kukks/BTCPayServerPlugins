using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
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
        await client.ConnectAndWaitUntilConnected(ct);
        _ = client.ListenForMessages();
        var tcs = new TaskCompletionSource();
       
        var ids = evts.Select(evt => evt.Id).ToHashSet();
        client.InvalidMessageReceived += (sender, tuple) =>
        {
            Console.WriteLine(tuple);
            
        };
        client.OkReceived += (sender, tuple) =>
        {
            if (ids.RemoveWhere(s => s == tuple.eventId)> 0 && !ids.Any())
            {
                tcs.TrySetResult();   
            }
        };
        client.EventsReceived += (sender, tuple) =>
        {
            if (ids.RemoveWhere(s => tuple.events.Any(@event => @event.Id == s)) > 0 && !ids.Any())
            {
                tcs.TrySetResult();
            }
        };
        await client.CreateSubscription("ack", new[]
        {
            new NostrSubscriptionFilter()
            {
                Ids = ids.ToArray()
            }
        }, ct);
        foreach (var evt in evts)
        {
            await client.PublishEvent(evt, ct);
        }
        await tcs.Task.WithCancellation(ct);
        await client.CloseSubscription("ack", ct);
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
            PublicKey = privateKey.CreatePubKey().ToXOnlyPubKey().ToHex(),
            CreatedAt = DateTimeOffset.UtcNow,
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
        
        var nostrClient = new NostrClient(relayUri, socket => socket.Options.Proxy = httpClientHandler?.Proxy);
        await nostrClient.CreateSubscription("nostr-wabisabi-coordinators",
            new[]
            {
                new NostrSubscriptionFilter()
                {
                    Kinds = new[] {Kind}, Since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)),
                    ExtensionData = new Dictionary<string, JsonElement>()
                    {
                        ["type"] = JsonSerializer.SerializeToElement(TypeTagValue),
                        ["network"] = JsonSerializer.SerializeToElement(currentNetwork.Name.ToLower())
                    }
                }
            }, cancellationToken);
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await nostrClient.ConnectAndWaitUntilConnected(cts.Token);
        _ = nostrClient.ListenForMessages();
        var result = new List<NostrEvent>();
        var tcs = new TaskCompletionSource();
        Stopwatch stopwatch = new();
        stopwatch.Start();
        nostrClient.EoseReceived += (sender, s) =>
        {
            tcs.SetResult();
        };
        nostrClient.EventsReceived += (sender, tuple) =>
        {
            stopwatch.Restart();
            result.AddRange(tuple.events);
        };
        while (!tcs.Task.IsCompleted && !cts.IsCancellationRequested &&
               stopwatch.ElapsedMilliseconds < 10000)
        {
            await Task.Delay(1000, cts.Token);
        }

        nostrClient.Dispose();

        var network = currentNetwork.Name
            .ToLower();
        return result.Where(@event =>
            @event.PublicKey != ourPubKey &&
            @event.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(15) &&
            @event.Verify() &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == EndpointTagIdentifier &&
                tag.Data.Any(s => Uri.IsWellFormedUriString(s, UriKind.Absolute))) &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == TypeTagIdentifier &&
                tag.Data.FirstOrDefault() == TypeTagValue) &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == NetworkTagIdentifier && tag.Data.FirstOrDefault() == network)
        ).Select(@event => new DiscoveredCoordinator()
        {
            Description = @event.Content,
            Name = @event.PublicKey,
            Uri = new Uri(@event.GetTaggedData("endpoint")
                .First(s => Uri.IsWellFormedUriString(s, UriKind.Absolute)))
        }).ToList();
    }
}
