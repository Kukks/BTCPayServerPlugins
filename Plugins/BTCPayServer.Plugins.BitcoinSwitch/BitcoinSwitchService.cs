using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Crowdfund;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Storage.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.FileSeller
{
    
    
    
    public class BitcoinSwitchEvent
    {
        public string AppId { get; set; }
        public string Message { get; set; }
        
    }
    
    
    public class BitcoinSwitchService : EventHostedServiceBase
    {
        private readonly AppService _appService;
        private readonly InvoiceRepository _invoiceRepository;
        public BitcoinSwitchService(EventAggregator eventAggregator,
            ILogger<BitcoinSwitchService> logger,
            AppService appService,
            InvoiceRepository invoiceRepository) : base(eventAggregator, logger)
        {
            _appService = appService;
            _invoiceRepository = invoiceRepository;
        }

        public ConcurrentMultiDictionary<string, WebSocket> AppToSockets { get; } = new();
        

        protected override void SubscribeToEvents()
        {
            Subscribe<InvoiceEvent>();
            Subscribe<BitcoinSwitchEvent>();
            base.SubscribeToEvents();
        }

        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            if (evt is BitcoinSwitchEvent bitcoinSwitchEvent)
            {
                if (AppToSockets.TryGetValues(bitcoinSwitchEvent.AppId, out var sockets))
                {
                    foreach (var socket in sockets)
                    {
                        try
                        {
                            await socket.SendAsync(
                                new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(bitcoinSwitchEvent.Message)),
                                WebSocketMessageType.Text, true, cancellationToken);
                        }
                        catch (Exception e)
                        {

                        }
                    }
                }
            }

            if (evt is not InvoiceEvent invoiceEvent) return;
            List<AppCartItem> cartItems = null;
            if (invoiceEvent.Name is not (InvoiceEvent.Completed or InvoiceEvent.MarkedCompleted
                or InvoiceEvent.Confirmed))
            {
                return;
            }

            var appIds = AppService.GetAppInternalTags(invoiceEvent.Invoice);

            if (!appIds.Any())
            {
                return;
            }

            if (invoiceEvent.Invoice.Metadata.AdditionalData.TryGetValue("bitcoinswitchactivated", out var activated))
            {
                return;
            }

            if ((!string.IsNullOrEmpty(invoiceEvent.Invoice.Metadata.ItemCode) ||
                 AppService.TryParsePosCartItems(invoiceEvent.Invoice.Metadata.PosData, out cartItems)))
            {
                var items = cartItems ?? new List<AppCartItem>();
                if (!string.IsNullOrEmpty(invoiceEvent.Invoice.Metadata.ItemCode) &&
                    !items.Exists(cartItem => cartItem.Id == invoiceEvent.Invoice.Metadata.ItemCode))
                {
                    items.Add(new AppCartItem()
                    {
                        Id = invoiceEvent.Invoice.Metadata.ItemCode,
                        Count = 1,
                        Price = invoiceEvent.Invoice.Price
                    });
                }

                var apps = (await _appService.GetApps(appIds)).Select(data =>
                {
                    switch (data.AppType)
                    {
                        case PointOfSaleAppType.AppType:
                            var possettings = data.GetSettings<PointOfSaleSettings>();
                            return (Data: data, Settings: (object) possettings,
                                Items: AppService.Parse(possettings.Template));
                        case CrowdfundAppType.AppType:
                            var cfsettings = data.GetSettings<CrowdfundSettings>();
                            return (Data: data, Settings: cfsettings,
                                Items: AppService.Parse(cfsettings.PerksTemplate));
                        default:
                            return (null, null, null);
                    }
                }).Where(tuple => tuple.Data != null && tuple.Items.Any(item =>
                    item.AdditionalData?.ContainsKey("bitcoinswitch_gpio") is true &&
                    items.Exists(cartItem => cartItem.Id == item.Id)));


                foreach (var valueTuple in apps)
                {
                    foreach (var item1 in valueTuple.Items.Where(item =>
                                 item.AdditionalData?.ContainsKey("bitcoinswitch_gpio") is true &&
                                 items.Exists(cartItem => cartItem.Id == item.Id)))
                    {
                        var appId = valueTuple.Data.Id;
                        var gpio = item1.AdditionalData["bitcoinswitch_gpio"].Value<string>();
                        var duration = item1.AdditionalData.TryGetValue("bitcoinswitch_duration", out var durationObj) &&
                                       durationObj.Type == JTokenType.Integer
                            ? durationObj.Value<string>()
                            : "5000";

                        PushEvent(new BitcoinSwitchEvent()
                        {
                            AppId = appId,
                            Message = $"{gpio}-{duration}.0"
                        });

                    }
                }


                invoiceEvent.Invoice.Metadata.SetAdditionalData("bitcoinswitchactivated", "true");
                await _invoiceRepository.UpdateInvoiceMetadata(invoiceEvent.InvoiceId, invoiceEvent.Invoice.StoreId,
                    invoiceEvent.Invoice.Metadata.ToJObject());
                
                
            }

            await base.ProcessEvent(evt, cancellationToken);
        }
    }
}