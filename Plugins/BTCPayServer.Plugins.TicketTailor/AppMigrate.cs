using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.TicketTailor;

public class AppMigrate : IStartupTask
{
    private readonly StoreRepository _storeRepository;
    private readonly AppService _appService;
    private readonly ApplicationDbContextFactory _contextFactory;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;

    public AppMigrate(StoreRepository storeRepository, AppService appService,
        ApplicationDbContextFactory contextFactory, BTCPayNetworkProvider btcPayNetworkProvider)
    {
        _storeRepository = storeRepository;
        _appService = appService;
        _contextFactory = contextFactory;
        _btcPayNetworkProvider = btcPayNetworkProvider;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var existingSettings =
            await _storeRepository.GetSettingsAsync<TicketTailorSettings>("TicketTailorSettings");
        foreach (var setting in existingSettings)
        {
            var app = new AppData()
            {
                Created = DateTimeOffset.UtcNow,
                Name = "Ticket Tailor",
                AppType = TicketTailorApp.AppType,
                StoreDataId = setting.Key,
                TagAllInvoices = false,
                Archived = false,
                Settings = JsonConvert.SerializeObject(setting.Value)
            };
            await _appService.UpdateOrCreateApp(app);
            await using var ctx = _contextFactory.CreateContext();
            var invoices = await ctx.Invoices
                .Include(data => data.InvoiceSearchData)
                .Where(data => data.StoreDataId == setting.Key && data.InvoiceSearchData.Any(searchData => searchData.Value == "tickettailor")).ToListAsync(cancellationToken: cancellationToken);
            foreach (var invoice in invoices)
            { 
                var entity = invoice.GetBlob();
                entity.Metadata.SetAdditionalData("appId", app.Id);
                entity.InternalTags.Add(AppService.GetAppInternalTag(app.Id));
                InvoiceRepository.AddToTextSearch(ctx, invoice, AppService.GetAppSearchTerm(app) );
                invoice.SetBlob(entity);
            }
            await ctx.SaveChangesAsync(cancellationToken);
                
            await _storeRepository.UpdateSetting<TicketTailorSettings>(setting.Key, "TicketTailorSettings", null);
        }
    }
}