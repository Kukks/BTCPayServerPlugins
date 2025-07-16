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
        public List<SwitchAction> SwitchSettings { get; set; }
    }

    public class BitcoinSwitchService : EventHostedServiceBase
    {
        private readonly AppService _appService;
        private readonly InvoiceRepository _invoiceRepository;

        public BitcoinSwitchService(
            EventAggregator eventAggregator,
            ILogger<BitcoinSwitchService> logger,
            AppService appService,
            InvoiceRepository invoiceRepository)
            : base(eventAggregator, logger)
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
                _ = HandleGPIOMessages(cancellationToken, bitcoinSwitchEvent);
                return;
            }

            if (evt is not InvoiceEvent invoiceEvent) return;
List<AppCartItem> cartItems = null;
            if (invoiceEvent.Name is not (InvoiceEvent.Completed or InvoiceEvent.MarkedCompleted
                or InvoiceEvent.Confirmed))
            {
                return;
            }
             //
             // invoiceEvent.Invoice.Metadata.AdditionalData
             //    .TryGetValue("bitcoinswitchsettings", out var explicitBitcoinswitchSettings);

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
                    item.AdditionalData?.ContainsKey("bitcoinswitch") is true &&
                    items.Exists(cartItem => cartItem.Id == item.Id)));


                foreach (var valueTuple in apps)
                {
                    foreach (var item1 in valueTuple.Items.Where(item =>
                                 item.AdditionalData?.ContainsKey("bitcoinswitch") is true &&
                                 items.Exists(cartItem => cartItem.Id == item.Id)))
                    {
                        var appId = valueTuple.Data.Id;
                        var gpio = item1.AdditionalData["bitcoinswitch"].Value<string>();

                        if (ParseActions(gpio) is { } actions)
                            PushEvent(new BitcoinSwitchEvent()
                            {
                                AppId = appId,
                                SwitchSettings = actions
                            });
                    }
                }
                // if(explicitBitcoinswitchSettings is not null)
                // {
                //     if (ParseActions(explicitBitcoinswitchSettings.Value<string>()) is { } actions)
                //         PushEvent(new BitcoinSwitchEvent()
                //         {
                //             SwitchSettings = actions
                //         });
                // }
                
                invoiceEvent.Invoice.Metadata.SetAdditionalData("bitcoinswitchactivated", "true");
                
                await _invoiceRepository.UpdateInvoiceMetadata(invoiceEvent.InvoiceId, invoiceEvent.Invoice.StoreId,
                    invoiceEvent.Invoice.Metadata.ToJObject());
                
                
            }

            await base.ProcessEvent(evt, cancellationToken);
        }

        private async Task HandleGPIOMessages(CancellationToken cancellationToken, BitcoinSwitchEvent bitcoinSwitchEvent)
        {


            var actions = bitcoinSwitchEvent.SwitchSettings;
            try
            {
                // Execute each action sequentially
                foreach (var action in actions)
                {
                    if (action.IsDelay)
                    {
                        // Wait for specified delay
                        await Task.Delay(action.DelayMs, cancellationToken);
                    }
                    else
                    {
                        // Send pin-duration command
                        var message = $"{action.Pin}-{action.Duration}";
                        var buffer = System.Text.Encoding.UTF8.GetBytes(message);

                        if (!AppToSockets.TryGetValues(bitcoinSwitchEvent.AppId, out var sockets))
                            return;
                        foreach (var socket in sockets)
                        {
                            await socket.SendAsync(
                                new ArraySegment<byte>(buffer),
                                WebSocketMessageType.Text,
                                true,
                                cancellationToken);
                        }
                                
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.PayServer.LogError(ex, "Error sending BitcoinSwitchEvent to socket");
            }
        }

        /// <summary>
        /// Parses a settings string like "25-5000.0,delay 1000,23-200.0" into a sequence of actions.
        /// </summary>
        private List<SwitchAction>? ParseActions(string settings)
        {
            try
            {
                var actions = new List<SwitchAction>();
                var segments = settings.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var seg in segments.Select(s => s.Trim()))
                {
                    if (seg.StartsWith("delay ", StringComparison.OrdinalIgnoreCase))
                    {
                        // Delay segment
                        var parts = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 && int.TryParse(parts[1], out var ms))
                        {
                            actions.Add(SwitchAction.Delay(ms));
                        }
                    }
                    else
                    {
                        // Pin-duration segment
                        var parts = seg.Split('-', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2 
                            && int.TryParse(parts[0], out var pin)
                            && double.TryParse(parts[1], out var duration))
                        {
                            actions.Add(SwitchAction.Command(pin, duration));
                        }
                    }
                }
                return actions;
            }
            catch (Exception e)
            {
               Logs.PayServer.LogError(e, "Error parsing BitcoinSwitchEvent settings");
                return null;
            }
            
            
        }
    }

    /// <summary>
    /// Represents either a delay or a pin-duration command.
    /// </summary>
    public class SwitchAction
    {
        public bool IsDelay { get; }
        public int DelayMs { get; }
        public int Pin { get; }
        public double Duration { get; }

        private SwitchAction(bool isDelay, int delayMs, int pin, double duration)
        {
            IsDelay = isDelay;
            DelayMs = delayMs;
            Pin = pin;
            Duration = duration;
        }

        public static SwitchAction Delay(int ms) => new(true, ms, 0, 0);
        public static SwitchAction Command(int pin, double duration) => new(false, 0, pin, duration);
    }
}
