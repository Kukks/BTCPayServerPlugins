using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Mails;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MimeKit;
using Newtonsoft.Json.Linq;
using static System.String;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorService : EventHostedServiceBase
{
    private readonly AppService _appService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TicketTailorService> _logger;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly EmailSenderFactory _emailSenderFactory;

    public TicketTailorService(
        AppService appService,
        IHttpClientFactory httpClientFactory,ILogger<TicketTailorService> logger,
        EventAggregator eventAggregator, InvoiceRepository invoiceRepository, EmailSenderFactory emailSenderFactory) : base(eventAggregator, logger)
    {
        _appService = appService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _invoiceRepository = invoiceRepository;
        _emailSenderFactory = emailSenderFactory;
    }




    internal class IssueTicket
    {
        public string InvoiceId { get; set; }
        public string AppId { get; set; }
        public InvoiceEntity InvoiceEntity { get; set; }
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<IssueTicket>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is InvoiceEvent invoiceEvent)
        {
            if (!new[] {InvoiceStatus.Settled, InvoiceStatus.Expired, InvoiceStatus.Invalid}.Contains(invoiceEvent
                    .Invoice.GetInvoiceState().Status.ToModernStatus()))
            {
                return;
            }
            var apps = AppService.GetAppInternalTags(invoiceEvent.Invoice);
            var internalTags = invoiceEvent.Invoice.GetInternalTags("");
            var possibleTicketTailorTags = apps.ToDictionary(s=>s,s => AppService.GetAppOrderId(TicketTailorApp.AppType, s));

            var matchedApps = possibleTicketTailorTags.Where(pair => internalTags.Contains(pair.Value))
                .Select(pair => pair.Key);
            foreach (var matchedApp in matchedApps)
            {
                PushEvent(new IssueTicket()
                {
                    AppId = matchedApp,
                    InvoiceId = invoiceEvent.InvoiceId,
                    InvoiceEntity = invoiceEvent.Invoice
                });
            }

            return;
        }

        if (evt is not IssueTicket issueTicket)
            return;

        async Task HandleIssueTicketError(JToken posData, string e, InvoiceEntity invoiceEntity ,
            InvoiceRepository invoiceRepository)
        {
            
            posData["Error"] =
                $"Ticket could not be created. You should refund customer.{Environment.NewLine}{e}";
            invoiceEntity.Metadata.PosData = posData.ToString();
            await invoiceRepository.UpdateInvoiceMetadata(invoiceEntity.Id, invoiceEntity.StoreId,
                invoiceEntity.Metadata.ToJObject());
            try
            {
                await invoiceRepository.MarkInvoiceStatus(invoiceEntity.Id, InvoiceStatus.Invalid);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    $"Failed to update invoice {invoiceEntity.Id} status from {invoiceEntity.Status} to Invalid after failing to issue ticket from ticket tailor");
            }
        }

        try
        {
            var app = await _appService.GetApp(issueTicket.AppId, TicketTailorApp.AppType);
            var settings = app?.GetSettings<TicketTailorSettings>();
            if (settings is null || settings.ApiKey is null)
            {
                return;
            }

            issueTicket.InvoiceEntity ??= await _invoiceRepository.GetInvoice(issueTicket.InvoiceId);
            if (issueTicket.InvoiceEntity?.GetInternalTags("")?.Contains(AppService.GetAppOrderId(app)) is not true)
            {
                return;
            }
            var holdId = issueTicket.InvoiceEntity.Metadata.GetMetadata<string?>("holdId");
            var status = issueTicket.InvoiceEntity.Status.ToModernStatus();
            if (new[] {InvoiceStatus.Invalid, InvoiceStatus.Expired}.Contains(status))
            {
                if (IsNullOrEmpty(holdId))
                {
                    return;
                }

                if (!await new TicketTailorClient(_httpClientFactory, settings.ApiKey).DeleteHold(holdId)) return;
                issueTicket.InvoiceEntity.Metadata.SetMetadata<string>("holdId", null);
                issueTicket.InvoiceEntity.Metadata.SetMetadata("holdId_deleted", holdId);
                await _invoiceRepository.UpdateInvoiceMetadata(issueTicket.InvoiceId,
                    issueTicket.InvoiceEntity.StoreId, issueTicket.InvoiceEntity.Metadata.ToJObject());

                return;
            }

            if (status!= InvoiceStatus.Settled)
            {
                return;
            }

            var ticketIds = issueTicket.InvoiceEntity.Metadata.GetMetadata<string[]>("ticketIds");
            if (ticketIds is not null)
            {
                return;
            }
            if (IsNullOrEmpty(holdId))
            {
                return;
            }

            var email = issueTicket.InvoiceEntity.Metadata.BuyerEmail;
            var name = issueTicket.InvoiceEntity.Metadata.BuyerName;

            JObject posData = null;
           try
           {
               posData = JObject.Parse( issueTicket.InvoiceEntity.Metadata.PosData);
           }
           catch
           { 
               posData ??= new JObject();
           }
            var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
            try
            {
                var tickets = new List<TicketTailorClient.IssuedTicket>();
                var errors = new List<string>();
                
                var hold = await client.GetHold(holdId);
                if (hold is null)
                {
                    await HandleIssueTicketError(posData, "The hold created for this invoice was not found", issueTicket.InvoiceEntity, _invoiceRepository);

                    return;
                    
                }
                var holdOriginalAmount = hold?.TotalOnHold;
                while (hold?.TotalOnHold > 0)
                {
                    foreach (var tt in hold.Quantities.Where(quantity => quantity.Quantity > 0))
                    {
                    
                        var ticketResult = await client.CreateTicket(new TicketTailorClient.IssueTicketRequest()
                        {
                            Reference = issueTicket.InvoiceId,
                            Email = email,
                            EventId = settings.EventId,
                            HoldId = holdId,
                            FullName = name,
                            TicketTypeId = tt.TicketTypeId
                        });
                        if (ticketResult.error is null)
                        {
                            tickets.Add(ticketResult.Item1);
                        
                        }
                        else
                        {
                        
                            errors.Add(ticketResult.error);
                        }
                        hold = await client.GetHold(holdId);
                    }
                }
                

                if (tickets.Count != holdOriginalAmount)
                {
                    await HandleIssueTicketError(posData, $"Not all the held tickets were issued because: {Join(",", errors)}", issueTicket.InvoiceEntity, _invoiceRepository);

                    return;
                }
                issueTicket.InvoiceEntity.Metadata.SetMetadata("ticketIds", tickets.Select(issuedTicket => issuedTicket.Id).ToArray());

                posData["Ticket Id"] =  new JArray(issueTicket.InvoiceEntity.Metadata.GetMetadata<string[]>("ticketIds"));
                issueTicket.InvoiceEntity.Metadata.PosData = posData.ToString();
                await _invoiceRepository.UpdateInvoiceMetadata(issueTicket.InvoiceId, issueTicket.InvoiceEntity.StoreId,
                    issueTicket.InvoiceEntity.Metadata.ToJObject());

                
                try
                {

                    var emailSender = await _emailSenderFactory.GetEmailSender(issueTicket.InvoiceEntity.StoreId);
                    emailSender.SendEmail(MailboxAddress.Parse(email), "Your ticket is available now.",
                        $"Your payment has been settled and the event ticket has been issued successfully. Please go to <a href='{issueTicket.InvoiceEntity.RedirectURL}'>{issueTicket.InvoiceEntity.RedirectURL}</a>");
                }
                catch (Exception e)
                {
                    // ignored
                }
            }
            catch (Exception e)
            {
                await HandleIssueTicketError(posData, e.Message, issueTicket.InvoiceEntity, _invoiceRepository);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue ticket");
        }
    }
}