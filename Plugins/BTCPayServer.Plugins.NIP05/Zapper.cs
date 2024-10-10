using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NBitcoin;
using NNostr.Client;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BTCPayServer.Plugins.NIP05;

public class Zapper : IHostedService
{
    record PendingZapEvent(string[] relays, NostrEvent nostrEvent);

    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly EventAggregator _eventAggregator;
    private readonly Nip5Controller _nip5Controller;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<Zapper> _logger;
    private readonly SettingsRepository _settingsRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private IEventAggregatorSubscription _subscription;
    private readonly ConcurrentBag<PendingZapEvent> _pendingZapEvents = new();
    private readonly NostrClientPool _nostrClientPool;

    public async Task<ZapperSettings> GetSettings()
    {
        var result = await _settingsRepository.GetSettingAsync<ZapperSettings>("Zapper");

        if (result is not null)
        {
            result.ZapperPrivateKey ??= Convert.ToHexString(RandomUtils.GetBytes(32));
            return result;
        }

        result = new ZapperSettings(Convert.ToHexString(RandomUtils.GetBytes(32)));
        await _settingsRepository.UpdateSetting(result, "Zapper");

        return result;
    }


    public Zapper(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        EventAggregator eventAggregator, 
        Nip5Controller nip5Controller, 
        IMemoryCache memoryCache, 
        ILogger<Zapper> logger, 
        SettingsRepository settingsRepository, 
        InvoiceRepository invoiceRepository,
        NostrClientPool nostrClientPool)
    {
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _eventAggregator = eventAggregator;
        _nip5Controller = nip5Controller;
        _memoryCache = memoryCache;
        _logger = logger;
        _settingsRepository = settingsRepository;
        _invoiceRepository = invoiceRepository;
        _nostrClientPool = nostrClientPool;
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
                var relaysToZap =relaysToConnectTo.
                    ToDictionary(s => s, s => pendingZaps.Where(@event => @event.relays.Contains(s)).Select(@event => @event.nostrEvent).ToArray())
                    .Chunk(5);

                foreach (var chunk in relaysToZap)
                {
                    await Task.WhenAll(chunk.Select(async relay =>
                    {
                        try
                        {
                            _logger.LogInformation($"Zapping {relay.Value.Length} to {relay.Key}");
                            var cts = new CancellationTokenSource();
                            cts.CancelAfter(TimeSpan.FromSeconds(30));
                            var pool= await 
                                _nostrClientPool.GetClientAndConnect(new []{new Uri(relay.Key)}, cts.Token);
                            using var c = pool.Item2;
                            await pool.Item1.SendEventsAndWaitUntilReceived(relay.Value, cts.Token);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Error zapping {relay.Value.Length} events to {relay.Key}");
                        }
                    }));
                }
                
                    
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            //we used to have some waiting logic so that we dont open a websocket to every relay for every individual zap only.
            //but people need their instant gratification so we removed it.
            // var waitingToken = CancellationTokenSource.CreateLinkedTokenSource();
            // waitingToken.CancelAfter(TimeSpan.FromSeconds(5));
            // while (!waitingToken.IsCancellationRequested)
            // {
            //     if (_pendingZapEvents.Count > 10)
            //     {
            //         waitingToken.Cancel();
            //     }
            //     else
            //     {
            //         try
            //         {
            //
            //             await Task.Delay(100, waitingToken.Token);
            //         }
            //         catch (TaskCanceledException e)
            //         {
            //             break;
            //         }
            //     }
            // }
        }
    }

    private async Task Subscription(InvoiceEvent arg)
    {
        if (arg.EventCode != InvoiceEventCode.Completed && arg.EventCode != InvoiceEventCode.MarkedCompleted)
            return;
        var pmi = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
        var pm = arg.Invoice.GetPaymentPrompt(pmi);
        if (pm is null)
        {
            return;
        }
        if(!_memoryCache.TryGetValue(Nip05Plugin.GetZapRequestCacheKey(arg.Invoice.Id), out var zapRequestEntry) || zapRequestEntry is not StringValues zapRequest)
        {
            return;
        }
        
        var settings = await GetSettings();
        
        var zapRequestEvent = JsonSerializer.Deserialize<NostrEvent>(zapRequest);
        var relays = zapRequestEvent.Tags.Where(tag => tag.TagIdentifier == "relays").SelectMany(tag => tag.Data).ToArray();
        
        var tags = zapRequestEvent.Tags.Where(a => a.TagIdentifier.Length == 1).ToList();

        
        tags.AddRange(new[]
        {
            new NostrEventTag
            {
                TagIdentifier = "bolt11",
                Data = new() {pm.Destination}
            },

            new NostrEventTag()
            {
                TagIdentifier = "description",
                Data = new() {zapRequest}
            }
        });

        var userNostrSettings = await _nip5Controller.GetForStore(arg.Invoice.StoreId);
        var key = !string.IsNullOrEmpty(userNostrSettings?.PrivateKey)
            ? NostrExtensions.ParseKey(userNostrSettings?.PrivateKey)
            : settings.ZappingKey; 
        
        var zapReceipt = new NostrEvent()
        {
            Kind = 9735,
            Content = zapRequestEvent.Content,
            Tags = tags
        };

        zapReceipt = await zapReceipt.ComputeIdAndSignAsync(key);
        relays = relays.Concat(userNostrSettings?.Relays ?? Array.Empty<string>()).Distinct().ToArray();
        arg.Invoice.Metadata.SetAdditionalData("Nostr", new Dictionary<string,string>()
        {
            {"Zap Request", zapRequestEvent.Id},
            {"Zap Receipt", zapReceipt.Id},
            {"Relays", string.Join(',', relays)}
        });
        await _invoiceRepository.UpdateInvoiceMetadata(arg.InvoiceId, arg.Invoice.StoreId, arg.Invoice.Metadata.ToJObject());
        _pendingZapEvents.Add(new PendingZapEvent(relays, zapReceipt));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}