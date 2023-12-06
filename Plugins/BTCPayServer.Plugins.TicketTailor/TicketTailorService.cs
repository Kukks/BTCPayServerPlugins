using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Mails;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MimeKit;
using Newtonsoft.Json.Linq;
using static System.String;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorService : EventHostedServiceBase
{
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TicketTailorService> _logger;

    private readonly EmailSenderFactory _emailSenderFactory;

    private readonly LinkGenerator _linkGenerator;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly AppService _appService;

    public TicketTailorService(IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory,
         ILogger<TicketTailorService> logger,
        EmailSenderFactory emailSenderFactory ,
        LinkGenerator linkGenerator,
        EventAggregator eventAggregator, InvoiceRepository invoiceRepository,
        AppService appService) : base(eventAggregator, logger)
    {
        _memoryCache = memoryCache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _emailSenderFactory = emailSenderFactory;
        _linkGenerator = linkGenerator;
        _invoiceRepository = invoiceRepository;
        _appService = appService;
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
            case InvoiceEvent invoiceEvent when invoiceEvent.Invoice.Metadata.OrderId != "tickettailor" || !new []{InvoiceStatus.Settled, InvoiceStatus.Expired, InvoiceStatus.Invalid}.Contains(invoiceEvent.Invoice.GetInvoiceState().Status.ToModernStatus()):
                return;
            case InvoiceEvent invoiceEvent:
                
                if(_memoryCache.TryGetValue($"{nameof(TicketTailorService)}_{invoiceEvent.Invoice.Id}_issue_check_from_ui", out _))return;
                
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

        async Task HandleIssueTicketError(string e, InvoiceEntity invoiceEntity, InvoiceLogs invoiceLogs, bool setInvalid = true)
        {
            invoiceLogs.Write( $"Ticket could not be created. You should refund customer.{Environment.NewLine}{e}", InvoiceEventData.EventSeverity.Error);
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
                    "The ticket tailor integration is misconfigured and BTCPay Server cannot connect to Ticket Tailor.", invoice, invLogs, false);
                return;
            }
            if (new[] {InvoiceStatus.Invalid, InvoiceStatus.Expired}.Contains(invoice.Status.ToModernStatus()))
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
                        await _invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId, invoice.Metadata.ToJObject());
                    }
                }

                return;
            }

            if (invoice.Status.ToModernStatus() != InvoiceStatus.Settled)
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
                
                await HandleIssueTicketError( "There was no hold associated with this invoice. Maybe this invoice was marked as invalid before?", invoice, invLogs);
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
                    await HandleIssueTicketError( "The hold created for this invoice was not found", invoice, invLogs);

                    return;
                    
                }
                var holdOriginalAmount = hold?.TotalOnHold;
                
                invLogs.Write( $"Issuing {holdOriginalAmount} tickets for hold {holdId}", InvoiceEventData.EventSeverity.Info);
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
                            invLogs.Write($"Issued ticket {ticketResult.Item1.Id} {ticketResult.Item1.Reference}", InvoiceEventData.EventSeverity.Info);
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
                    await HandleIssueTicketError( $"Not all the held tickets were issued because: {Join(",", errors)}", invoice, invLogs);

                    return;
                }
                await _invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId, invoice.Metadata.ToJObject());
                await _invoiceRepository.AddInvoiceLogs(invoice.Id, invLogs);
                var uri = new Uri(btcpayUrl);
                var url =
                    _linkGenerator.GetUriByAction("Receipt",
                        "TicketTailor",
                        new {invoiceId = invoice.Id},
                        uri.Scheme,
                        new HostString(uri.Host),
                        uri.AbsolutePath);

                try
                {
                    var sender = await _emailSenderFactory.GetEmailSender(issueTicket.Invoice.StoreId);
                    sender.SendEmail(MailboxAddress.Parse(email), "Your ticket is available now.",
                        $"Your payment has been settled and the event ticket has been issued successfully. Please go to <a href='{url}'>{url}</a>");
                }
                catch (Exception e)
                {
                    // ignored
                }
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
}