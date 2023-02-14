using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorApp : IApp
{
    public const string AppType = "TicketTailor";
    private readonly BTCPayServerOptions _options;
    private readonly LinkGenerator _linkGenerator;
    private readonly IHttpClientFactory _httpClientFactory;

    public TicketTailorApp(BTCPayServerOptions options, LinkGenerator linkGenerator, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _linkGenerator = linkGenerator;
        _httpClientFactory = httpClientFactory;
    }
    public string Description => "Ticket Tailor Event";
    public string Type => AppType;
    public string ConfigureLink(string appId)
    {
        return  _linkGenerator.GetPathByAction(nameof(UITicketTailorController.UpdateTicketTailorSettings),
            "UITicketTailor", new {appId}, _options.RootPath);
    }

    public async Task<SalesStats> GetSaleStates(AppData app, InvoiceEntity[] paidInvoices, int numberOfDays)
    {
        var settings = app.GetSettings<TicketTailorSettings>();
        if (string.IsNullOrEmpty(settings.ApiKey) || string.IsNullOrEmpty(settings.EventId))
        {
            return new SalesStats();
        }
            
        using var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
        var evt = await client.GetEvent(settings.EventId);
        if(evt is null) {
            return new SalesStats();
        }

        var tickets = evt.TicketTypes.ToDictionary(type => type.Id);

        var series = paidInvoices
            .Aggregate(new List<AppService.InvoiceStatsItem>(), (list, entity) =>
            {
                var ids = entity.Metadata.GetMetadata<string[]>("ticketIds");
                if (ids?.Any() is not true)
                {
                    return list;
                }

                ids = ids.Where(s => tickets.ContainsKey(s)).ToArray();
                list.AddRange(ids.Select( s => new AppService.InvoiceStatsItem()
                {
                        
                    ItemCode = tickets[s].Name,
                    FiatPrice = tickets[s].Price,
                    Date = entity.InvoiceTime.Date
                }));
                return list;

            })
            .GroupBy(entity => entity.Date)
            .Select(entities => new SalesStatsItem
            {
                Date = entities.Key,
                Label = entities.Key.ToString("MMM dd", CultureInfo.InvariantCulture),
                SalesCount = entities.Count()
            });

        // fill up the gaps
        foreach (var i in Enumerable.Range(0, numberOfDays))
        {
            var date = (DateTimeOffset.UtcNow - TimeSpan.FromDays(i)).Date;
            if (!series.Any(e => e.Date == date))
            {
                series = series.Append(new SalesStatsItem
                {
                    Date = date,
                    Label = date.ToString("MMM dd", CultureInfo.InvariantCulture)
                });
            }
        }

        return new SalesStats()
        {
            SalesCount = series.Sum(item => item.SalesCount),
            Series = series
        };

    }

    public async Task<IEnumerable<ItemStats>> GetItemStats(AppData appData, InvoiceEntity[] paidInvoices)
    {
            
        var settings = appData.GetSettings<TicketTailorSettings>();
        if (string.IsNullOrEmpty(settings.ApiKey) || string.IsNullOrEmpty(settings.EventId))
        {
            return Array.Empty<ItemStats>();
        }
            
        using var client = new TicketTailorClient(_httpClientFactory, settings.ApiKey);
        var evt = await client.GetEvent(settings.EventId);
        if(evt is null) {
            return Array.Empty<ItemStats>();
        }

        var tickets = evt.TicketTypes.ToDictionary(type => type.Id);
            
            
        var itemCount = paidInvoices
            .Where(entity => entity.Currency.Equals(evt.Currency, StringComparison.OrdinalIgnoreCase) && (
                // The POS data is present for the cart view, where multiple items can be bought
                !string.IsNullOrEmpty(entity.Metadata.PosData) ||
                // The item code should be present for all types other than the cart and keypad
                !string.IsNullOrEmpty(entity.Metadata.ItemCode)
            ))
            .Aggregate(new List<AppService.InvoiceStatsItem>(), (list, entity) =>
            {
                var ids = entity.Metadata.GetMetadata<string[]>("ticketIds");
                if (ids?.Any() is not true)
                {
                    return list;
                }

                ids = ids.Where(s => tickets.ContainsKey(s)).ToArray();
                list.AddRange(ids.Select( s => new AppService.InvoiceStatsItem()
                {
                        
                    ItemCode = tickets[s].Name,
                    FiatPrice = tickets[s].Price,
                    Date = entity.InvoiceTime.Date
                }));
                return list;

            })
            .GroupBy(entity => entity.ItemCode)
            .Select(entities =>
            {
                var total = entities.Sum(entity => entity.FiatPrice);
                var itemCode = entities.Key;
                tickets.TryGetValue(itemCode, out var ticket);
                return new ItemStats
                {
                    ItemCode = itemCode,
                    Title = ticket?.Name ?? itemCode,
                    SalesCount = entities.Count(),
                    Total = total,
                    TotalFormatted = $"{total.ShowMoney(2)} {evt.Currency}"
                };
            })
            .OrderByDescending(stats => stats.SalesCount);

        return itemCount;
            
    }

    public async Task<object> GetInfo(AppData app)
    {
        var config = app.GetSettings<TicketTailorSettings>();
        try
        {
            if (config?.ApiKey is not null && config?.EventId is not null)
            {
                var client = new TicketTailorClient(_httpClientFactory, config.ApiKey);
                var evt = await client.GetEvent(config.EventId);
                return evt is null ? null : new TicketTailorViewModel() {Event = evt, Settings = config};
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public Task SetDefaultSettings(AppData appData, string defaultCurrency)
    {
        appData.SetSettings(new TicketTailorSettings());
        return Task.CompletedTask;
    }

    public string ViewLink(AppData app)
    {
        return _linkGenerator.GetPathByAction("View", "UITicketTailor", new {appId = app.Id}, _options.RootPath);
    }
}