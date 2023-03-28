using System;
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
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.7.7"}
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
    }

    public class Zapper : IHostedService
    {
        private readonly EventAggregator _eventAggregator;
        private readonly Nip5Controller _nip5Controller;
        private IEventAggregatorSubscription _subscription;

        public Zapper(EventAggregator eventAggregator, Nip5Controller nip5Controller)
        {
            _eventAggregator = eventAggregator;
            _nip5Controller = nip5Controller;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _subscription = _eventAggregator.SubscribeAsync<InvoiceEvent>(Subscription);
            return Task.CompletedTask;
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

            var zapRequest = arg.Invoice.Metadata.GetAdditionalData<string>("zapRequest");
            if (zapRequest is null)
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
            
            
            await Task.WhenAll(relays.Concat(settings.settings.Relays?? Array.Empty<string>()).Distinct().Select(async relay =>
            {
                try
                {
                    
                    var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    var tcs = new TaskCompletionSource();
                    using var c = new NostrClient(new Uri(relay));
                    await c.ConnectAndWaitUntilConnected(cts.Token);
                    
                    c.OkReceived += (sender, okargs) =>
                    {
                        if(okargs.eventId == zapReceipt.Id)
                            tcs.SetResult();
                    };
                    await c.PublishEvent(zapReceipt, cts.Token);
                    await tcs.Task.WaitAsync(cts.Token);
                    await c.Disconnect();
                }
                catch (Exception e)
                {
                }
            }));
            
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
        public override string Hook => "lnurlp";

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
        private readonly InvoiceRepository _invoiceRepository;

        public LnurlDescriptionFilter(IHttpContextAccessor httpContextAccessor,
            Nip5Controller nip5Controller, LightningAddressService lightningAddressService,
            InvoiceRepository invoiceRepository)
        {
            _httpContextAccessor = httpContextAccessor;
            _nip5Controller = nip5Controller;
            _lightningAddressService = lightningAddressService;
            _invoiceRepository = invoiceRepository;
        }

        public override string Hook => "lnurlp-description";

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
                            throw new InvalidOperationException("Invalid zap note, kind must be 9734");
                        }

                        if (!parsedNote.Verify())
                        {
                            throw new InvalidOperationException("Zap note sig check failed");
                        }

                        var invoice = await _invoiceRepository.GetInvoice(invoiceId.ToString());

                        invoice.Metadata.SetAdditionalData("zapRequest", nostr);
                        await _invoiceRepository.UpdateInvoiceMetadata(invoiceId.ToString(), invoice.StoreId,
                            invoice.Metadata.ToJObject());
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