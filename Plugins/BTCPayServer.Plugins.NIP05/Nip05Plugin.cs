using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using LNURL;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NNostr.Client;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BTCPayServer.Plugins.NIP05
{
    public class Nip05Plugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.9.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Nip05Nav",
                "store-integrations-nav"));
            applicationBuilder.AddSingleton<IPluginHookFilter, LnurlDescriptionFilter>();
            applicationBuilder.AddSingleton<IPluginHookFilter, LnurlFilter>();
            applicationBuilder.AddHostedService<Zapper>();
            base.Execute(applicationBuilder);
        }

        public static string GetZapRequestCacheKey(string invoiceid)
        {
            return nameof(Nip05Plugin)+ invoiceid;
        }
    }

    public class Zapper : IHostedService
    {
        record PendingZapEvent(string[] relays, NostrEvent nostrEvent);
        
        private readonly EventAggregator _eventAggregator;
        private readonly Nip5Controller _nip5Controller;
        private readonly IMemoryCache _memoryCache;
        private IEventAggregatorSubscription _subscription;
        private ConcurrentBag<PendingZapEvent> _pendingZapEvents = new();

        public Zapper(EventAggregator eventAggregator, Nip5Controller nip5Controller, IMemoryCache memoryCache)
        {
            _eventAggregator = eventAggregator;
            _nip5Controller = nip5Controller;
            _memoryCache = memoryCache;
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
                    var pendingZaps = _pendingZapEvents.Take(Range.All).ToArray();
                    var relaysToConnectTo = pendingZaps.SelectMany(@event => @event.relays).Distinct();
                    var relaysToZap =relaysToConnectTo.ToDictionary(s => s, s => pendingZaps.Where(@event => @event.relays.Contains(s)).Select(@event => @event.nostrEvent).ToArray());

                    await Task.WhenAll(relaysToZap.Select(async relay =>
                    {
                        try
                        {
                    
                            var cts = new CancellationTokenSource();
                            cts.CancelAfter(TimeSpan.FromSeconds(30));
                            var tcs = new TaskCompletionSource();
                            using var c = new NostrClient(new Uri(relay.Key));
                            await c.ConnectAndWaitUntilConnected(cts.Token);
                            var pendingOksOnIds = relay.Value.Select(a => a.Id).ToHashSet();
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
                        await Task.Delay(100, waitingToken.Token);
                    }
                }
            }
        }

        private async Task Subscription(InvoiceEvent arg)
        {
            if (arg.EventCode != InvoiceEventCode.Completed)
                return;
            var pm = arg.Invoice.GetPaymentMethod(new PaymentMethodId("BTC", PaymentTypes.LNURLPay));
            if (pm is null)
            {
                return;
            }
            if(!_memoryCache.TryGetValue(Nip05Plugin.GetZapRequestCacheKey(arg.Invoice.Id), out var zapRequestEntry) || zapRequestEntry is not string zapRequest)
            {
                return;
            }

            var pmd = (LNURLPayPaymentMethodDetails) pm.GetPaymentMethodDetails();
            var name = pmd.ConsumedLightningAddress.Split("@")[0];
            var settings = await _nip5Controller.Get(name);
            if (settings.storeId != arg.Invoice.StoreId)
            {
                return;
            }

            if (string.IsNullOrEmpty(settings.settings.PrivateKey))
            {
                return;
            }

            var key = NostrExtensions.ParseKey(settings.settings.PrivateKey);
            
            var zapRequestEvent = JsonSerializer.Deserialize<NostrEvent>(zapRequest);
            var relays = zapRequestEvent.GetTaggedData("relays");
            
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
                PublicKey = settings.settings.PubKey,
                Content = zapRequestEvent.Content,
                Tags = tags
            };


            await zapReceipt.ComputeIdAndSignAsync(key);
            
            _pendingZapEvents.Add(new PendingZapEvent(relays.Concat(settings.settings.Relays?? Array.Empty<string>()).Distinct().ToArray(), zapReceipt));
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _subscription?.Dispose();
            return Task.CompletedTask;
        }
    }

    public class LnurlFilter : PluginHookFilter<LNURLPayRequest>
    {
        private readonly Nip5Controller _nip5Controller;
        private readonly LightningAddressService _lightningAddressService;
        public override string Hook => "modify-lnurlp-request";

        public LnurlFilter(Nip5Controller nip5Controller, LightningAddressService lightningAddressService)
        {
            _nip5Controller = nip5Controller;
            _lightningAddressService = lightningAddressService;
        }

        public override async Task<LNURLPayRequest> Execute(LNURLPayRequest arg)
        {
            var name = arg.ParsedMetadata.FirstOrDefault(pair => pair.Key == "text/identifier").Value
                ?.ToLowerInvariant().Split("@")[0];
            if (string.IsNullOrEmpty(name))
            {
                return arg;
            }

            var lnAddress = await _lightningAddressService.ResolveByAddress(name);
            if (lnAddress is null)
            {
                return arg;
            }

            var nip5 = await _nip5Controller.Get(name);
            if (nip5.storeId != lnAddress.StoreDataId)
            {
                return arg;
            }

            arg.NostrPubkey = nip5.settings.PubKey;
            arg.AllowsNostr = true;
            return arg;
        }
    }

    public class LnurlDescriptionFilter : PluginHookFilter<string>
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly Nip5Controller _nip5Controller;
        private readonly LightningAddressService _lightningAddressService;
        private readonly IMemoryCache _memoryCache;

        public LnurlDescriptionFilter(IHttpContextAccessor httpContextAccessor,
            Nip5Controller nip5Controller, LightningAddressService lightningAddressService,
            InvoiceRepository invoiceRepository, IMemoryCache memoryCache)
        {
            _httpContextAccessor = httpContextAccessor;
            _nip5Controller = nip5Controller;
            _lightningAddressService = lightningAddressService;
            _memoryCache = memoryCache;
        }

        public override string Hook => "modify-lnurlp-description";

        public override async Task<string> Execute(string arg)
        {
            try
            {
                if (_httpContextAccessor.HttpContext.Request.Query.TryGetValue("nostr", out var nostr) &&
                    _httpContextAccessor.HttpContext.Request.RouteValues.TryGetValue("invoiceId", out var invoiceId))
                {
                    var metadata = JsonConvert.DeserializeObject<string[][]>(arg);
                    var username = metadata
                        .FirstOrDefault(strings => strings.FirstOrDefault()?.Equals("text/identifier") is true)
                        ?.FirstOrDefault()?.ToLowerInvariant().Split("@")[0];
                    if (string.IsNullOrEmpty(username))
                    {
                        return arg;
                    }

                    var lnAddress = await _lightningAddressService.ResolveByAddress(username);


                    var user = await _nip5Controller.Get(username);
                    if (user.storeId is not null)
                    {
                        if (user.storeId != lnAddress.StoreDataId)
                        {
                            return arg;
                        }

                        var parsedNote = System.Text.Json.JsonSerializer.Deserialize<NostrEvent>(nostr);
                        if (parsedNote?.Kind != 9734)
                        {
                            return arg;
                        }

                        if (!parsedNote.Verify())
                        {
                            return arg;
                        }

                        var entry =_memoryCache.CreateEntry(Nip05Plugin.GetZapRequestCacheKey(invoiceId.ToString()));
                        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                        entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                        entry.SetValue(nostr);
                        return nostr;
                    }
                }
            }
            catch (Exception e)
            {
            }

            return arg;
        }
    }
}