using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.DataErasure
{
    public class DataErasureService : IHostedService
    {
        private readonly IStoreRepository _storeRepository;
        private readonly ILogger<DataErasureService> _logger;
        private readonly InvoiceRepository _invoiceRepository;
        private readonly ApplicationDbContextFactory _dbContextFactory;

        public DataErasureService(IStoreRepository storeRepository, ILogger<DataErasureService> logger,
            InvoiceRepository invoiceRepository, ApplicationDbContextFactory dbContextFactory)
        {
            _storeRepository = storeRepository;
            _logger = logger;
            _invoiceRepository = invoiceRepository;
            _dbContextFactory = dbContextFactory;
        }

        public async Task<DataErasureSettings> Get(string storeId)
        {
            return await _storeRepository.GetSettingAsync<DataErasureSettings>(storeId,
                nameof(DataErasureSettings));
        }

        public async Task Set(string storeId, DataErasureSettings settings, bool clearDate = false)
        {
            _cts?.Cancel();
            await _runningLock.WaitAsync();
            var existing = await Get(storeId);
            settings.LastRunCutoff = clearDate? null:  existing?.LastRunCutoff;
            await SetCore(storeId, settings);
            _runningLock.Release();
            _cts = new CancellationTokenSource();
            _ = Run();
        }

        private async Task SetCore(string storeId, DataErasureSettings settings)
        {
            await _storeRepository.UpdateSetting(storeId, nameof(DataErasureSettings), settings);
        }

        public bool IsRunning { get; private set; }
        private readonly SemaphoreSlim _runningLock = new(1, 1);

        private async Task Run()
        {
            while (!_cts.IsCancellationRequested)
            {
                await _runningLock.WaitAsync(_cts.Token);
                IsRunning = true;


                var settings =
                    await _storeRepository.GetSettingsAsync<DataErasureSettings>(nameof(DataErasureSettings));
                foreach (var setting in settings.Where(setting => setting.Value.Enabled))
                {
                    var count = 0;
                    var cutoffDate = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(setting.Value.DaysToKeep));
                    if (setting.Value.EntirelyEraseInvoice)
                    {
                        await using var db = _dbContextFactory.CreateContext();
                        db.Invoices.RemoveRange(db.Invoices.Where(i => i.StoreDataId == setting.Key && i.Created < cutoffDate && (setting.Value.LastRunCutoff == null || i.Created > setting.Value.LastRunCutoff)));
                        count = await db.SaveChangesAsync(_cts.Token);
                    }
                    else
                    {



                        var skip = 0;
                        while (true)
                        {
                            var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery()
                            {
                                StartDate = setting.Value.LastRunCutoff,
                                EndDate = cutoffDate,
                                StoreId = new[] {setting.Key},
                                Skip = skip,
                                Take = 100
                            }, _cts.Token);



                            foreach (var invoice in invoices)
                            {
                                //replace all buyer info with "erased"
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerAddress1))
                                    invoice.Metadata.BuyerAddress1 = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerAddress2))
                                    invoice.Metadata.BuyerAddress2 = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerCity))
                                    invoice.Metadata.BuyerCity = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerCountry))
                                    invoice.Metadata.BuyerCountry = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerEmail))
                                    invoice.Metadata.BuyerEmail = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerName))
                                    invoice.Metadata.BuyerName = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerPhone))
                                    invoice.Metadata.BuyerPhone = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerState))
                                    invoice.Metadata.BuyerState = "erased";
                                if (!string.IsNullOrEmpty(invoice.Metadata.BuyerZip))
                                    invoice.Metadata.BuyerZip = "erased";
                                await _invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId,
                                    invoice.Metadata.ToJObject());
                                count++;
                            }

                            if (invoices.Length < 100)
                            {
                                break;
                            }

                            skip += 100;
                        }
                    }

                    if (count > 0)
                        _logger.LogInformation($"Erased {count} invoice data for store {setting.Key}");
                    setting.Value.LastRunCutoff = cutoffDate;
                    await SetCore(setting.Key, setting.Value);
                }

                IsRunning = false;


                _runningLock.Release();
                await Task.Delay(TimeSpan.FromHours(1), _cts.Token);
            }

            try
            {
                _runningLock.Release();
            }
            catch (Exception e)
            {
            }
        }

        private CancellationTokenSource _cts;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Run();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}