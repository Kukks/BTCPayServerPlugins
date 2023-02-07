using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using static System.String;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorService : EventHostedServiceBase
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IMemoryCache _memoryCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStoreRepository _storeRepository;
    private readonly ILogger<TicketTailorService> _logger;
    private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
    private readonly LinkGenerator _linkGenerator;

    public TicketTailorService(ISettingsRepository settingsRepository, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory,
        IStoreRepository storeRepository, ILogger<TicketTailorService> logger,
        IBTCPayServerClientFactory btcPayServerClientFactory, LinkGenerator linkGenerator,
        EventAggregator eventAggregator) : base(eventAggregator, logger)
    {
        _settingsRepository = settingsRepository;
        _memoryCache = memoryCache;
        _httpClientFactory = httpClientFactory;
        _storeRepository = storeRepository;
        _logger = logger;
        _btcPayServerClientFactory = btcPayServerClientFactory;
        _linkGenerator = linkGenerator;
    }


    public async Task<TicketTailorSettings> GetTicketTailorForStore(string storeId)
    {
        var k = $"{nameof(TicketTailorSettings)}_{storeId}";
        return await _memoryCache.GetOrCreateAsync(k, async _ =>
        {
            var res = await _storeRepository.GetSettingAsync<TicketTailorSettings>(storeId,
                nameof(TicketTailorSettings));
            if (res is not null) return res;
            res = await _settingsRepository.GetSettingAsync<TicketTailorSettings>(k);

            if (res is not null)
            {
                await SetTicketTailorForStore(storeId, res);
            }

            await _settingsRepository.UpdateSetting<TicketTailorSettings>(null, k);
            return res;
        });
    }

    public async Task SetTicketTailorForStore(string storeId, TicketTailorSettings TicketTailorSettings)
    {
        var k = $"{nameof(TicketTailorSettings)}_{storeId}";
        await _storeRepository.UpdateSetting(storeId, nameof(TicketTailorSettings), TicketTailorSettings);
        _memoryCache.Set(k, TicketTailorSettings);
    }


    internal class IssueTicket
    {
        public string InvoiceId { get; set; }
        public string StoreId { get; set; }
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<IssueTicket>();
        base.SubscribeToEvents();
    }

    public async Task<BTCPayServerClient> CreateClient(string storeId)
    {
        return await _btcPayServerClientFactory.Create(null, new[] {storeId});
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is InvoiceEvent invoiceEvent)
        {
            if (invoiceEvent.Invoice.Metadata.OrderId != "tickettailor" || !new []{InvoiceStatus.Settled, InvoiceStatus.Expired, InvoiceStatus.Invalid}.Contains(invoiceEvent.Invoice.GetInvoiceState().Status.ToModernStatus()))
            {
                return;
            }

            evt = new IssueTicket() {InvoiceId = invoiceEvent.InvoiceId, StoreId = invoiceEvent.Invoice.StoreId};
        }

        if (evt is not IssueTicket issueTicket)
            return;

        async Task HandleIssueTicketError(JToken posData, string e, InvoiceData invoiceData,
            BTCPayServerClient btcPayClient)
        {
            posData["Error"] =
                $"Ticket could not be created. You should refund customer.{Environment.NewLine}{e}";
            invoiceData.Metadata["posData"] = posData;
            await btcPayClient.UpdateInvoice(issueTicket.StoreId, invoiceData.Id,
                new UpdateInvoiceRequest() {Metadata = invoiceData.Metadata}, cancellationToken);
            try
            {
                await btcPayClient.MarkInvoiceStatus(issueTicket.StoreId, invoiceData.Id,
                    new MarkInvoiceStatusRequest() {Status = InvoiceStatus.Invalid}, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    $"Failed to update invoice {invoiceData.Id} status from {invoiceData.Status} to Invalid after failing to issue ticket from ticket tailor");
            }
        }

        InvoiceData invoice = null;
        try
        {
            var settings = await GetTicketTailorForStore(issueTicket.StoreId);
            if (settings is null || settings.ApiKey is null)
            {
                return;
            }

            var btcPayClient = await CreateClient(issueTicket.StoreId);
            invoice = await btcPayClient.GetInvoice(issueTicket.StoreId, issueTicket.InvoiceId, cancellationToken);

            if (new[] {InvoiceStatus.Invalid, InvoiceStatus.Expired}.Contains(invoice.Status))
            {
                if (invoice.Metadata.TryGetValue("holdId", out var jHoldIdx) &&
                    jHoldIdx.Value<string>() is { } holdIdx)
                {
                    if (await new TicketTailorClient(_httpClientFactory, settings.ApiKey).DeleteHold(holdIdx))
                    {
                        invoice.Metadata.Remove("holdId");
                        invoice.Metadata.Add("holdId_deleted", holdIdx);
                        await btcPayClient.UpdateInvoice(issueTicket.StoreId, issueTicket.InvoiceId,
                            new UpdateInvoiceRequest()
                            {
                                Metadata = invoice.Metadata
                            }, cancellationToken);
                    }
                }

                return;
            }

            if (invoice.Status != InvoiceStatus.Settled)
            {
                return;
            }

            if (invoice.Metadata.TryGetValue("ticketIds", out var jTicketId) &&
                jTicketId.Values<string>() is { } ticketIds)
            {
                return;
            }

            if (!invoice.Metadata.TryGetValue("holdId", out var jHoldId) ||
                jHoldId.Value<string>() is not { } holdId)
            {
                return;
            }

            if (!invoice.Metadata.TryGetValue("btcpayUrl", out var jbtcpayUrl) ||
                jbtcpayUrl.Value<string>() is not { } btcpayUrl)
            {
                return;
            }

            var email = invoice.Metadata["buyerEmail"].ToString();
            var name = invoice.Metadata["buyerName"]?.ToString();
            invoice.Metadata.TryGetValue("posData", out var posData);
            posData ??= new JObject();
            var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
            try
            {
                var tickets = new List<TicketTailorClient.IssuedTicket>();
                var errors = new List<string>();
                
                var hold = await client.GetHold(holdId);
                if (hold is null)
                {
                    await HandleIssueTicketError(posData, "The hold created for this invoice was not found", invoice, btcPayClient);

                    return;
                    
                }
                var holdOriginalAmount = hold?.TotalOnHold;
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
                    await HandleIssueTicketError(posData, $"Not all the held tickets were issued because: {Join(",", errors)}", invoice, btcPayClient);

                    return;
                }

                invoice.Metadata["ticketIds"] =
                    new JArray(tickets.Select(issuedTicket => issuedTicket.Id));

                posData["Ticket Id"] = invoice.Metadata["ticketIds"];
                invoice.Metadata["posData"] = posData;
                await btcPayClient.UpdateInvoice(issueTicket.StoreId, invoice.Id,
                    new UpdateInvoiceRequest() {Metadata = invoice.Metadata}, cancellationToken);

                var uri = new Uri(btcpayUrl);
                var url =
                    _linkGenerator.GetUriByAction("Receipt",
                        "TicketTailor",
                        new {issueTicket.StoreId, invoiceId = invoice.Id},
                        uri.Scheme,
                        new HostString(uri.Host),
                        uri.AbsolutePath);

                try
                {
                    await btcPayClient.SendEmail(issueTicket.StoreId,
                        new SendEmailRequest()
                        {
                            Subject = "Your ticket is available now.",
                            Email = email,
                            Body =
                                $"Your payment has been settled and the event ticket has been issued successfully. Please go to <a href='{url}'>{url}</a>"
                        }, cancellationToken);
                }
                catch (Exception e)
                {
                    // ignored
                }
            }
            catch (Exception e)
            {
                await HandleIssueTicketError(posData, e.Message, invoice, btcPayClient);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue ticket");
        }
    }
}