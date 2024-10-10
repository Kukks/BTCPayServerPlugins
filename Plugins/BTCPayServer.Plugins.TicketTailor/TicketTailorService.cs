using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.HostedServices.Webhooks;
using BTCPayServer.Logging;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Mails;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MimeKit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static System.String;
using InvoiceData = BTCPayServer.Data.InvoiceData;
using WebhookDeliveryData = BTCPayServer.Data.WebhookDeliveryData;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorService : EventHostedServiceBase, IWebhookProvider
{
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TicketTailorService> _logger;

    private readonly EmailSenderFactory _emailSenderFactory;

    private readonly LinkGenerator _linkGenerator;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly AppService _appService;
    private readonly WebhookSender _webhookSender;
    private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
    private IWebhookProvider _webhookProviderImplementation;

    public TicketTailorService(IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory,
        ILogger<TicketTailorService> logger,
        EmailSenderFactory emailSenderFactory,
        LinkGenerator linkGenerator,
        EventAggregator eventAggregator, InvoiceRepository invoiceRepository,
        AppService appService,  
        WebhookSender webhookSender,BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings) : base(eventAggregator, logger)
    {
        _memoryCache = memoryCache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _emailSenderFactory = emailSenderFactory;
        _linkGenerator = linkGenerator;
        _invoiceRepository = invoiceRepository;
        _appService = appService;
        _webhookSender = webhookSender;
        _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
    }

    private class IssueTicket
    {
        public InvoiceEntity Invoice { get; set; }
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<IssueTicket>();
        base.SubscribeToEvents();
    }


    public async Task CheckAndIssueTicket(string id)
    {
        await _memoryCache.GetOrCreateAsync($"{nameof(TicketTailorService)}_{id}_issue_check_from_ui", async entry =>
        {
            var invoice = await _invoiceRepository.GetInvoice(id);
            PushEvent(new IssueTicket()
            {
                Invoice = invoice
            });
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return true;
        });

    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        switch (evt)
        {
            case InvoiceEvent invoiceEvent when invoiceEvent.Invoice.Metadata.OrderId != "tickettailor" ||
                                                !new[]
                                                {
                                                    InvoiceStatus.Settled, InvoiceStatus.Expired, InvoiceStatus.Invalid
                                                }.Contains(invoiceEvent.Invoice.GetInvoiceState().Status):
                return;
            case InvoiceEvent invoiceEvent:

                if (_memoryCache.TryGetValue(
                        $"{nameof(TicketTailorService)}_{invoiceEvent.Invoice.Id}_issue_check_from_ui", out _)) return;

                await _memoryCache.GetOrCreateAsync(
                    $"{nameof(TicketTailorService)}_{invoiceEvent.Invoice.Id}_issue_check_from_ui", async entry =>
                    {
                        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                        return true;
                    });

                evt = new IssueTicket() {Invoice = invoiceEvent.Invoice};
                break;
        }

        if (evt is not IssueTicket issueTicket)
            return;

        async Task HandleIssueTicketError(string e, InvoiceEntity invoiceEntity, InvoiceLogs invoiceLogs,
            bool setInvalid = true)
        {
            invoiceLogs.Write($"Ticket could not be created. You should refund customer.{Environment.NewLine}{e}",
                InvoiceEventData.EventSeverity.Error);
            await _invoiceRepository.AddInvoiceLogs(invoiceEntity.Id, invoiceLogs);

            try
            {
                await _invoiceRepository.MarkInvoiceStatus(invoiceEntity.Id, InvoiceStatus.Invalid);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    $"Failed to update invoice {invoiceEntity.Id} status from {invoiceEntity.Status} to Invalid after failing to issue ticket from ticket tailor");
            }
        }

        try
        {

            var invLogs = new InvoiceLogs();
            var appId = AppService.GetAppInternalTags(issueTicket.Invoice).First();
            var app = await _appService.GetApp(appId, TicketTailorApp.AppType);
            var settings = app.GetSettings<TicketTailorSettings>();
            var invoice = issueTicket.Invoice;
            if (settings?.ApiKey is null)
            {
                await HandleIssueTicketError(
                    "The ticket tailor integration is misconfigured and BTCPay Server cannot connect to Ticket Tailor.",
                    invoice, invLogs, false);
                return;
            }

            if (new[] {InvoiceStatus.Invalid, InvoiceStatus.Expired}.Contains(invoice.Status))
            {

                if (invoice.Metadata.AdditionalData.TryGetValue("holdId", out var jHoldIdx) &&
                    jHoldIdx.Value<string>() is { } holdIdx)
                {

                    await HandleIssueTicketError(
                        "Deleting the hold as the invoice is invalid/expired.", invoice, invLogs, false);
                    if (await new TicketTailorClient(_httpClientFactory, settings.ApiKey).DeleteHold(holdIdx))
                    {
                        invoice.Metadata.AdditionalData.Remove("holdId");
                        invoice.Metadata.AdditionalData.Add("holdId_deleted", holdIdx);
                        await _invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId,
                            invoice.Metadata.ToJObject());
                    }
                }

                return;
            }

            if (invoice.Status != InvoiceStatus.Settled)
            {
                return;
            }

            if (invoice.Metadata.AdditionalData.TryGetValue("ticketIds", out var jTicketId) &&
                jTicketId.Values<string>() is { } ticketIds)
            {
                return;
            }

            if (!invoice.Metadata.AdditionalData.TryGetValue("holdId", out var jHoldId) ||
                jHoldId.Value<string>() is not { } holdId)
            {

                await HandleIssueTicketError(
                    "There was no hold associated with this invoice. Maybe this invoice was marked as invalid before?",
                    invoice, invLogs);
                return;
            }

            if (!invoice.Metadata.AdditionalData.TryGetValue("btcpayUrl", out var jbtcpayUrl) ||
                jbtcpayUrl.Value<string>() is not { } btcpayUrl)
            {
                return;
            }

            var email = invoice.Metadata.AdditionalData["buyerEmail"].ToString();
            var name = invoice.Metadata.AdditionalData["buyerName"]?.ToString();

            var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
            try
            {
                var tickets = new List<TicketTailorClient.IssuedTicket>();
                var errors = new List<string>();

                var hold = await client.GetHold(holdId);
                if (hold is null)
                {
                    await HandleIssueTicketError("The hold created for this invoice was not found", invoice, invLogs);

                    return;

                }

                var holdOriginalAmount = hold?.TotalOnHold;

                invLogs.Write($"Issuing {holdOriginalAmount} tickets for hold {holdId}",
                    InvoiceEventData.EventSeverity.Info);
                while (hold?.TotalOnHold > 0)
                {
                    foreach (var tt in hold.Quantities.Where(quantity => quantity.Quantity > 0))
                    {

                        var ticketResult = await client.CreateTicket(new TicketTailorClient.IssueTicketRequest()
                        {
                            Reference = invoice.Id,
                            Email = email,
                            EventId = settings.EventId,
                            HoldId = holdId,
                            FullName = name,
                            TicketTypeId = tt.TicketTypeId
                        });
                        if (ticketResult.error is null)
                        {
                            tickets.Add(ticketResult.Item1);
                            invLogs.Write($"Issued ticket {ticketResult.Item1.Id} {ticketResult.Item1.Reference}",
                                InvoiceEventData.EventSeverity.Info);
                        }
                        else
                        {

                            errors.Add(ticketResult.error);
                        }

                        hold = await client.GetHold(holdId);
                    }
                }


                invoice.Metadata.AdditionalData["ticketIds"] =
                    new JArray(tickets.Select(issuedTicket => issuedTicket.Id));
                if (tickets.Count != holdOriginalAmount)
                {
                    await HandleIssueTicketError($"Not all the held tickets were issued because: {Join(",", errors)}",
                        invoice, invLogs);

                    return;
                }

                await _invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId,
                    invoice.Metadata.ToJObject());
                await _invoiceRepository.AddInvoiceLogs(invoice.Id, invLogs);
                var uri = new Uri(btcpayUrl);
                var receiptUrl =
                    _linkGenerator.GetUriByAction("Receipt",
                        "TicketTailor",
                        new {invoiceId = invoice.Id},
                        uri.Scheme,
                        new HostString(uri.Host),
                        uri.AbsolutePath);
                if (settings.SendEmail)
                {

                    

                    try
                    {
                        var sender = await _emailSenderFactory.GetEmailSender(issueTicket.Invoice.StoreId);
                        sender.SendEmail(MailboxAddress.Parse(email), "Your ticket is available now.",
                            $"Your payment has been settled and the event ticket has been issued successfully. Please go to <a href='{receiptUrl}'>{receiptUrl}</a>");
                    }
                    catch (Exception e)
                    {
                        // ignored
                    }

                }
                TicketTailorWebhookDeliveryRequest CreateDeliveryRequest(WebhookData? webhook)
                {
                    var webhookEvent = new WebhookTicketTailorEvent(TicketTailorTicketIssued, invoice.StoreId)
                    {
                        AppId = appId,
                        Tickets = tickets.Select(t => t.Id).ToArray(),
                        InvoiceId = invoice.Id
                    };
                    var delivery = webhook is null? null:  WebhookExtensions.NewWebhookDelivery(webhook.Id);
                    if (delivery is not null)
                    {
                        webhookEvent.DeliveryId = delivery.Id;
                        webhookEvent.OriginalDeliveryId = delivery.Id;
                        webhookEvent.Timestamp = delivery.Timestamp;
                    }
                    
                    return new TicketTailorWebhookDeliveryRequest(receiptUrl, invoice, webhook?.Id,
                        webhookEvent,
                        delivery,
                        webhook?.GetBlob(),_btcPayNetworkJsonSerializerSettings);
                }
                
                var webhooks = await _webhookSender.GetWebhooks(app.StoreDataId, TicketTailorTicketIssued);
                foreach (var webhook in webhooks)
                {
                    _webhookSender.EnqueueDelivery(CreateDeliveryRequest( webhook));
                }

                EventAggregator.Publish(CreateDeliveryRequest( null));
            }
            catch (Exception e)
            {
                await HandleIssueTicketError(e.Message, invoice, invLogs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue ticket");
        }
    }
    

    public const string TicketTailorTicketIssued = "TicketTailorTicketIssued";

    public Dictionary<string, string> GetSupportedWebhookTypes()
    {
        return new Dictionary<string, string>
        {
            {TicketTailorTicketIssued, "A ticket has been issued through ticket tailor"},
        };

    }

    public WebhookEvent CreateTestEvent(string type, params object[] args)
    {
        var storeId = args[0].ToString();
        return new WebhookTicketTailorEvent(type, storeId)
        {
            AppId = "__test__" + Guid.NewGuid() + "__test__",
            Tickets = new[] {"__test__" + Guid.NewGuid() + "__test__"}
        };
    }

    public class WebhookTicketTailorEvent : StoreWebhookEvent
    {
        public WebhookTicketTailorEvent(string type, string storeId)
        {
            if (!type.StartsWith("tickettailor", StringComparison.InvariantCultureIgnoreCase))
                throw new ArgumentException("Invalid event type", nameof(type));
            Type = type;
            StoreId = storeId;
        }

        
        [JsonProperty(Order = 2)] public string AppId { get; set; }
        [JsonProperty(Order = 3)] public string[] Tickets { get; set; }
        
        [JsonProperty(Order = 4)] public string InvoiceId { get; set; }
    }

    public class TicketTailorWebhookDeliveryRequest(
        string receiptUrl,
        InvoiceEntity invoice,
        string? webhookId,
        WebhookEvent webhookEvent,
        WebhookDeliveryData? delivery,
        WebhookBlob? webhookBlob,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings)
        : WebhookSender.WebhookDeliveryRequest(webhookId!, webhookEvent, delivery!, webhookBlob!)
    {
        public InvoiceEntity Invoice { get; } = invoice;

        public override Task<SendEmailRequest?> Interpolate(SendEmailRequest req,
            UIStoresController.StoreEmailRule storeEmailRule)
        {
            if (storeEmailRule.CustomerEmail &&
                MailboxAddressValidator.TryParse(Invoice.Metadata.BuyerEmail, out var bmb))
            {
                req.Email ??= string.Empty;
                req.Email += $",{bmb}";
            }

            
            
            req.Subject = Interpolate(req.Subject);
            req.Body = Interpolate(req.Body);
            return Task.FromResult(req)!;
        }

        private string Interpolate(string str)
        {
            var res =  str.Replace("{Invoice.Id}", Invoice.Id)
                .Replace("{Invoice.StoreId}", Invoice.StoreId)
                .Replace("{Invoice.Price}", Invoice.Price.ToString(CultureInfo.InvariantCulture))
                .Replace("{Invoice.Currency}", Invoice.Currency)
                .Replace("{Invoice.Status}", Invoice.Status.ToString())
                .Replace("{Invoice.AdditionalStatus}", Invoice.ExceptionStatus.ToString())
                .Replace("{Invoice.OrderId}", Invoice.Metadata.OrderId);


            res = InterpolateJsonField(str, "Invoice.Metadata", Invoice.Metadata.ToJObject());
            
            res = res.Replace("{TicketTailor.ReceiptUrl}", receiptUrl);
            
            return res;
        }
    }

}