#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Emails.Services;
using BTCPayServer.Plugins.Webhooks;
using BTCPayServer.Plugins.Webhooks.TriggerProviders;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorWebhookProvider(
    IMemoryCache memoryCache,
    InvoiceRepository invoiceRepository,
    EmailSenderFactory emailSenderFactory,
    ILogger<TicketTailorWebhookProvider> logger,
    LinkGenerator linkGenerator,
    EventAggregator eventAggregator,
    IHttpClientFactory httpClientFactory,
    AppService appService)  : WebhookTriggerProvider
{
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
    
    private record IssueTicket(InvoiceEntity Invoice);
    public const string TicketTailorTicketIssued = "TicketTailorTicketIssued";
    public override async Task<StoreWebhookEvent?> GetWebhookEventAsync(object evt)
    {
        evt = (await GetIssuedTicket(evt))!;
        if (evt is not IssueTicket issueTicket)
            return null;
        async Task HandleIssueTicketError(string e, InvoiceEntity invoiceEntity, InvoiceLogs invoiceLogs,
            bool setInvalid = true)
        {
            invoiceLogs.Write($"Ticket could not be created. You should refund customer.{Environment.NewLine}{e}",
                InvoiceEventData.EventSeverity.Error);
            await invoiceRepository.AddInvoiceLogs(invoiceEntity.Id, invoiceLogs);

            try
            {
                await invoiceRepository.MarkInvoiceStatus(invoiceEntity.Id, InvoiceStatus.Invalid);
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    $"Failed to update invoice {invoiceEntity.Id} status from {invoiceEntity.Status} to Invalid after failing to issue ticket from ticket tailor");
            }
        }
        
        try
        {

            var invLogs = new InvoiceLogs();
            var appId = AppService.GetAppInternalTags(issueTicket.Invoice).First();
            var app = await appService.GetApp(appId, TicketTailorApp.AppType);
            var settings = app.GetSettings<TicketTailorSettings>();
            var invoice = issueTicket.Invoice;
            if (settings?.ApiKey is null)
            {
                await HandleIssueTicketError(
                    "The ticket tailor integration is misconfigured and BTCPay Server cannot connect to Ticket Tailor.",
                    invoice, invLogs, false);
                return null;
            }

            if (new[] {InvoiceStatus.Invalid, InvoiceStatus.Expired}.Contains(invoice.Status))
            {

                if (invoice.Metadata.AdditionalData.TryGetValue("holdId", out var jHoldIdx) &&
                    jHoldIdx.Value<string>() is { } holdIdx)
                {

                    await HandleIssueTicketError(
                        "Deleting the hold as the invoice is invalid/expired.", invoice, invLogs, false);
                    if (await new TicketTailorClient(httpClientFactory, settings.ApiKey).DeleteHold(holdIdx))
                    {
                        invoice.Metadata.AdditionalData.Remove("holdId");
                        invoice.Metadata.AdditionalData.Add("holdId_deleted", holdIdx);
                        await invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId,
                            invoice.Metadata.ToJObject());
                    }
                }

                return null;
            }

            if (invoice.Status != InvoiceStatus.Settled)
            {
                return null;
            }

            if (invoice.Metadata.AdditionalData.TryGetValue("ticketIds", out var jTicketId) &&
                jTicketId.Values<string>() is { } ticketIds)
            {
                return null;
            }

            if (!invoice.Metadata.AdditionalData.TryGetValue("holdId", out var jHoldId) ||
                jHoldId.Value<string>() is not { } holdId)
            {

                await HandleIssueTicketError(
                    "There was no hold associated with this invoice. Maybe this invoice was marked as invalid before?",
                    invoice, invLogs);
                return null;
            }

            var mailboxAddress = InvoiceTriggerProvider.GetMailboxAddress(invoice.Metadata);
            if (mailboxAddress is null)
                return null;

            var client = new TicketTailorClient(httpClientFactory, settings.ApiKey);
            try
            {
                var tickets = new List<TicketTailorClient.IssuedTicket>();
                var errors = new List<string>();

                var hold = await client.GetHold(holdId);
                if (hold is null)
                {
                    await HandleIssueTicketError("The hold created for this invoice was not found", invoice, invLogs);

                    return null;
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
                            Email = mailboxAddress.Address,
                            EventId = settings.EventId,
                            HoldId = holdId,
                            FullName = mailboxAddress.Name,
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
                    await HandleIssueTicketError($"Not all the held tickets were issued because: {String.Join(",", errors)}",
                        invoice, invLogs);

                    return null;
                }

                await invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId,
                    invoice.Metadata.ToJObject());
                await invoiceRepository.AddInvoiceLogs(invoice.Id, invLogs);
                
                if (settings.SendEmail)
                {
                    // TODO: This should be refactored.
                    // "SendMail" should actually create a EmailRule
                    // for the webhook when the settings is set, this shouldn't send an email here,
                    // as this isn't customizable.
                    var receiptUrl = GetReceiptUrl(invoice);
                    try
                    {
                        var sender = await emailSenderFactory.GetEmailSender(issueTicket.Invoice.StoreId);
                        sender.SendEmail(mailboxAddress, "Your ticket is available now.",
                            $"Your payment has been settled and the event ticket has been issued successfully. Please go to <a href='{receiptUrl}'>{receiptUrl}</a>");
                    }
                    catch (Exception e)
                    {
                        // ignored
                    }
                }
                
                return new WebhookTicketTailorEvent(TicketTailorTicketIssued, invoice.StoreId)
                {
                    AppId = appId,
                    Tickets = tickets.Select(t => t.Id).ToArray(),
                    InvoiceId = invoice.Id
                };
            }
            catch (Exception e)
            {
                await HandleIssueTicketError(e.Message, invoice, invLogs);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to issue ticket");
            
        }
        return null;
    }

    private async Task<IssueTicket?> GetIssuedTicket(object evt)
    {
        switch (evt)
        {
            case InvoiceEvent invoiceEvent when invoiceEvent.Invoice.Metadata.OrderId != "tickettailor" ||
                                                !new[]
                                                {
                                                    InvoiceStatus.Settled, InvoiceStatus.Expired, InvoiceStatus.Invalid
                                                }.Contains(invoiceEvent.Invoice.GetInvoiceState().Status):
                return null;
            case InvoiceEvent invoiceEvent:

                if (memoryCache.TryGetValue(
                        $"{nameof(TicketTailorWebhookProvider)}_{invoiceEvent.Invoice.Id}_issue_check_from_ui", out _)) return null;

                await memoryCache.GetOrCreateAsync(
                    $"{nameof(TicketTailorWebhookProvider)}_{invoiceEvent.Invoice.Id}_issue_check_from_ui", async entry =>
                    {
                        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                        return true;
                    });

                evt = new IssueTicket(invoiceEvent.Invoice);
                break;
        }

        return evt as IssueTicket;
    }

    public override async Task<JObject> GetEmailModel(WebhookTriggerContext webhookTriggerContext)
    {
        var model = await base.GetEmailModel(webhookTriggerContext);
        var invoice = (await GetIssuedTicket(webhookTriggerContext.Event))?.Invoice;
        if (invoice is null)
            return model;
        InvoiceTriggerProvider.AddInvoiceToModel(model, invoice, linkGenerator);
        model["TicketTailor"] = new JObject()
        {
            ["ReceiptUrl"] = GetReceiptUrl(invoice)
        };
        return model;
    }

    private string? GetReceiptUrl(InvoiceEntity invoice)
    {
        var baseUrl = invoice.GetRequestBaseUrl();
        var receiptUrl = linkGenerator.GetUriByAction("Receipt",
            "TicketTailor",
            new { invoiceId = invoice.Id },
            baseUrl.Scheme,
            baseUrl.Host,
            baseUrl.PathBase);
        return receiptUrl;
    }

    public async Task CheckAndIssueTicket(string id)
    {
        await memoryCache.GetOrCreateAsync($"{nameof(TicketTailorWebhookProvider)}_{id}_issue_check_from_ui", async entry =>
        {
            var invoice = await invoiceRepository.GetInvoice(id);
            eventAggregator.Publish(new IssueTicket(invoice));
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return true;
        });
    }
}