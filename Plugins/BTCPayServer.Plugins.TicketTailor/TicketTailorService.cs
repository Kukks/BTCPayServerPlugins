using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorService : IHostedService
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
        IBTCPayServerClientFactory btcPayServerClientFactory, LinkGenerator linkGenerator)
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


    public Task<InvoiceData> Handle(string invoiceId, string storeId, Uri host)
    {
        var tcs = new TaskCompletionSource<InvoiceData>();
        _events.Writer.TryWrite(new IssueTicket() {Task = tcs, InvoiceId = invoiceId, StoreId = storeId, Host = host});
        return tcs.Task;
    }

    internal class IssueTicket
    {
        public string InvoiceId { get; set; }
        public string StoreId { get; set; }
        public TaskCompletionSource<InvoiceData?> Task { get; set; }
        public Uri Host { get; set; }
    }


    readonly Channel<IssueTicket> _events = Channel.CreateUnbounded<IssueTicket>();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = ProcessEvents(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task<BTCPayServerClient> CreateClient(string storeId, Uri host)
    {
        return await _btcPayServerClientFactory.Create(null, new []{storeId}, new DefaultHttpContext()
        {
            Request =
            {
                Scheme = host.Scheme,
                Host = new HostString(host.Host),
                Path = new PathString(host.AbsolutePath),
                PathBase = new PathString(),
            }
        });
    }

    private async Task ProcessEvents(CancellationToken cancellationToken)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken))
        {
            if (!_events.Reader.TryRead(out var evt)) continue;

            async Task HandleIssueTicketError(JToken posData, string e, InvoiceData invoiceData,
                BTCPayServerClient btcPayClient)
            {
                posData["Error"] =
                    $"Ticket could not be created. You should refund customer.{Environment.NewLine}{e}";
                invoiceData.Metadata["posData"] = posData;
                await btcPayClient.UpdateInvoice(evt.StoreId, invoiceData.Id,
                    new UpdateInvoiceRequest() {Metadata = invoiceData.Metadata}, cancellationToken);
                try
                {
                    await btcPayClient.MarkInvoiceStatus(evt.StoreId, invoiceData.Id,
                        new MarkInvoiceStatusRequest() {Status = InvoiceStatus.Invalid}, cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, $"Failed to update invoice {invoiceData.Id} status from {invoiceData.Status} to Invalid after failing to issue ticket from ticket tailor");
                }
            }

            InvoiceData invoice = null;
            try
            {
                var settings = await GetTicketTailorForStore(evt.StoreId);
                if (settings is null || settings.ApiKey is null)
                {
                    evt.Task.SetResult(null);
                    continue;
                }

                var btcPayClient = await CreateClient(evt.StoreId, evt.Host);
                invoice = await btcPayClient.GetInvoice(evt.StoreId, evt.InvoiceId, cancellationToken);
                if (invoice.Status != InvoiceStatus.Settled)
                {
                    evt.Task.SetResult(null);
                    continue;
                }

                if (invoice.Metadata.ContainsKey("ticketId"))
                {
                    evt.Task.SetResult(null);
                    continue;
                }

                var ticketTypeId = invoice.Metadata["ticketTypeId"].ToString();
                var email = invoice.Metadata["buyerEmail"].ToString();
                var name = invoice.Metadata["buyerName"]?.ToString();
                invoice.Metadata.TryGetValue("posData", out var posData);
                posData ??= new JObject();
                var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
                try
                {
                    var ticketResult = await client.CreateTicket(new TicketTailorClient.IssueTicketRequest()
                    {
                        Reference = invoice.Id,
                        Email = email,
                        EventId = settings.EventId,
                        TicketTypeId = ticketTypeId,
                        FullName = name,
                    });

                    if (ticketResult.Item2 is not null)
                    {
                        await HandleIssueTicketError(posData, ticketResult.Item2, invoice, btcPayClient);
                            
                        continue;
                    }
                        
                    var ticket = ticketResult.Item1;
                    invoice.Metadata["ticketId"] = ticket.Id;
                    invoice.Metadata["orderId"] = $"tickettailor_{ticket.Id}";

                    posData["Ticket Code"] = ticket.Barcode;
                    posData["Ticket Id"] = ticket.Id;
                    invoice.Metadata["posData"] = posData;
                    await btcPayClient.UpdateInvoice(evt.StoreId, invoice.Id,
                        new UpdateInvoiceRequest() {Metadata = invoice.Metadata}, cancellationToken);

                    var url =
                        _linkGenerator.GetUriByAction("Receipt",
                            "TicketTailor",
                            new {evt.StoreId, invoiceId = invoice.Id},
                            evt.Host.Scheme,
                            new HostString(evt.Host.Host),
                            evt.Host.AbsolutePath);

                    try
                    {
                        await btcPayClient.SendEmail(evt.StoreId,
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
            finally
            {
                evt.Task.SetResult(invoice);
            }
        }
    }
}
