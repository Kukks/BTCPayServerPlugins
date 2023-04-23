using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;

namespace BTCPayServer.Plugins.NIP05;

public record ZapperSettings(string ZapperPrivateKey)
{
    public ECPrivKey ZappingKey => NostrExtensions.ParseKey(ZapperPrivateKey);
    public ECXOnlyPubKey ZappingPublicKey => ZappingKey.CreateXOnlyPubKey();
    public string ZappingPublicKeyHex => ZappingPublicKey.ToHex();
    
}
public class Zapper : IHostedService
{
    record PendingZapEvent(string[] relays, NostrEvent nostrEvent);
        
    private readonly EventAggregator _eventAggregator;
    private readonly Nip5Controller _nip5Controller;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<Zapper> _logger;
    private readonly SettingsRepository _settingsRepository;
    private IEventAggregatorSubscription _subscription;
    private ConcurrentBag<PendingZapEvent> _pendingZapEvents = new();

    private async Task<ZapperSettings> GetSettings()
    { var result = await _settingsRepository.GetSettingAsync<ZapperSettings>( "Zapper");
        if (result is not null) return result;
        result = new ZapperSettings(Convert.ToHexString(RandomUtils.GetBytes(32)));
        await _settingsRepository.UpdateSetting(result, "Zapper");

        return result;
    }


    public Zapper(EventAggregator eventAggregator, 
        Nip5Controller nip5Controller, IMemoryCache memoryCache, ILogger<Zapper> logger, SettingsRepository settingsRepository, StoreRepository storeRepository)
    {
        _eventAggregator = eventAggregator;
        _nip5Controller = nip5Controller;
        _memoryCache = memoryCache;
        _logger = logger;
        _settingsRepository = settingsRepository;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventAggregator.SubscribeAsync<InvoiceEvent>(Subscription);
        _ = SendZapReceipts(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task SendZapReceipts(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_pendingZapEvents.Any())
            {
                _logger.LogInformation($"Attempting to send {_pendingZapEvents.Count} zap receipts");
                List<PendingZapEvent> pendingZaps = new();
                while (!_pendingZapEvents.IsEmpty)
                {
                    if (_pendingZapEvents.TryTake(out var pendingZap))
                    {
                        pendingZaps.Add(pendingZap);
                    }
                }
                var relaysToConnectTo = pendingZaps.SelectMany(@event => @event.relays).Distinct();
                var relaysToZap =relaysToConnectTo.ToDictionary(s => s, s => pendingZaps.Where(@event => @event.relays.Contains(s)).Select(@event => @event.nostrEvent).ToArray());

                await Task.WhenAll(relaysToZap.Select(async relay =>
                {
                    try
                    {
                    
                        _logger.LogInformation($"Zapping {relay.Value.Length} to {relay.Key}");
                        var cts = new CancellationTokenSource();
                        cts.CancelAfter(TimeSpan.FromSeconds(30));
                        var tcs = new TaskCompletionSource();
                        using var c = new NostrClient(new Uri(relay.Key));
                        await c.ConnectAndWaitUntilConnected(cts.Token);
                        
                        var pendingOksOnIds = relay.Value.Select(a => a.Id).ToHashSet();
                        var subscription = new NostrSubscriptionFilter()
                        {
                            Ids = pendingOksOnIds.ToArray()
                        };
                        c.EventsReceived+= (sender, args) =>
                        {
                            foreach (var nostrEvent in args.events)
                            {
                                if (nostrEvent.Id == "zap-confirmation")
                                {
                                    pendingOksOnIds.Remove(nostrEvent.Id);
                                    if (!pendingOksOnIds.Any())
                                    {
                                        tcs.SetResult();
                                    }
                                }
                            }
                        };
                        await c.CreateSubscription("zap-confirmations",new []{subscription}, cts.Token);
                        c.OkReceived += (sender, okargs) =>
                        {
                            pendingOksOnIds.Remove(okargs.eventId);
                            if(!pendingOksOnIds.Any())
                                tcs.SetResult();
                        };
                        foreach (var nostrEvent in relay.Value)
                        {
                            await c.PublishEvent(nostrEvent, cts.Token);
                                
                        }
                        await tcs.Task.WaitAsync(cts.Token);
                        await c.Disconnect();
                    }
                    catch (Exception e)
                    {
                    }
                }));
                    
            }
            var waitingToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitingToken.CancelAfter(TimeSpan.FromMinutes(2));
            while (!waitingToken.IsCancellationRequested)
            {
                if (_pendingZapEvents.Count > 10)
                {
                    waitingToken.Cancel();
                }
                else
                {
                    try
                    {

                        await Task.Delay(100, waitingToken.Token);
                    }
                    catch (TaskCanceledException e)
                    {
                        break;
                    }
                }
            }
        }
    }

    private async Task Subscription(InvoiceEvent arg)
    {
        if (arg.EventCode != InvoiceEventCode.Completed && arg.EventCode != InvoiceEventCode.MarkedCompleted)
            return;
        var pm = arg.Invoice.GetPaymentMethod(new PaymentMethodId("BTC", PaymentTypes.LNURLPay));
        if (pm is null)
        {
            return;
        }
        if(!_memoryCache.TryGetValue(Nip05Plugin.GetZapRequestCacheKey(arg.Invoice.Id), out var zapRequestEntry) || zapRequestEntry is not StringValues zapRequest)
        {
            return;
        }

        var pmd = (LNURLPayPaymentMethodDetails) pm.GetPaymentMethodDetails();
        var settings = await GetSettings();
        
        var zapRequestEvent = JsonSerializer.Deserialize<NostrEvent>(zapRequest);
        var relays = zapRequestEvent.Tags.Where(tag => tag.TagIdentifier == "relays").SelectMany(tag => tag.Data).ToArray();
        
        var tags = zapRequestEvent.Tags.Where(a => a.TagIdentifier.Length == 1).ToList();
        tags.Add(new()
        {
            TagIdentifier = "bolt11",
            Data = new() {pmd.BOLT11}
        });

        tags.Add(new()
        {
            TagIdentifier = "description",
            Data = new() {zapRequest}
        });

        var zapReceipt = new NostrEvent()
        {
            Kind = 9735,
            CreatedAt = DateTimeOffset.UtcNow,
            PublicKey = settings.ZappingPublicKeyHex,
            Content = zapRequestEvent.Content,
            Tags = tags
        };


        await zapReceipt.ComputeIdAndSignAsync(settings.ZappingKey);
        var userNostrSettings = await _nip5Controller.GetForStore(arg.Invoice.StoreId);
        _pendingZapEvents.Add(new PendingZapEvent(relays.Concat(userNostrSettings?.Relays?? Array.Empty<string>()).Distinct().ToArray(), zapReceipt));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}