using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using WalletWasabi.Backend.Controllers;

namespace BTCPayServer.Plugins.Wabisabi;

public class Nostr
{
    public static int Kind = 15750;

    public static async Task Publish(
        Uri relayUri,
        NostrEvent[] evts,
        CancellationToken cancellationToken )
    {

        var ct = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token)
            .Token;
        var client = new NostrClient(relayUri);
        await client.ConnectAndWaitUntilConnected(ct);
        _ = client.ListenForMessages();
        var tcs = new TaskCompletionSource();
       
        var ids = evts.Select(evt => evt.Id).ToHashSet();
        
        
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
                new() {TagIdentifier = "uri", Data = new List<string>() {new Uri(coordinatorUri, "plugins/wabisabi-coordinator").ToString()}},
                new() {TagIdentifier = "network", Data = new List<string>() {currentNetwork.Name.ToLower()}}
            }
        };
        
        await evt.ComputeIdAndSign(privateKey);
        return evt;
    }

    public static async Task<List<DiscoveredCoordinator>> Discover(
        Uri relayUri,
        Network currentNetwork,
        string ourPubKey,
        CancellationToken cancellationToken)
    {
        using var nostrClient = new NostrClient(relayUri);
        await nostrClient.CreateSubscription("nostr-wabisabi-coordinators",
            new[]
            {
                new NostrSubscriptionFilter()
                {
                    Kinds = new[] {Kind}, Since = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(1)),
                }
            }, cancellationToken);
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await nostrClient.ConnectAndWaitUntilConnected(cts.Token);
        _ = nostrClient.ListenForMessages();
        var result = new List<NostrEvent>();
        var tcs = new TaskCompletionSource();
        Stopwatch stopwatch = new();
        stopwatch.Start();
        nostrClient.MessageReceived += (sender, s) =>
        {
            if (JArray.Parse(s).FirstOrDefault()?.Value<string>() == "EOSE")
            {
                tcs.SetResult();
            }
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
                tag.TagIdentifier == "uri" &&
                tag.Data.Any(s => Uri.IsWellFormedUriString(s, UriKind.Absolute))) &&
            @event.Tags.Any(tag =>
                tag.TagIdentifier == "network" && tag.Data.FirstOrDefault() == network)
        ).Select(@event => new DiscoveredCoordinator()
        {
            Description = @event.Content,
            Name = @event.PublicKey,
            Uri = new Uri(@event.GetTaggedData("uri")
                .First(s => Uri.IsWellFormedUriString(s, UriKind.Absolute)))
        }).ToList();
    }
}
