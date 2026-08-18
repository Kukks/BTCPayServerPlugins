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
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Run(cts);
        }

        private async Task SetCore(string storeId, DataErasureSettings settings)
        {
            await _storeRepository.UpdateSetting(storeId, nameof(DataErasureSettings), settings);
        }

        public bool IsRunning { get; private set; }
        private readonly SemaphoreSlim _runningLock = new(1, 1);

        private async Task Run(CancellationTokenSource cts)
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await _runningLock.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                IsRunning = true;
                try
                {
                    var settings =
                        await _storeRepository.GetSettingsAsync<DataErasureSettings>(nameof(DataErasureSettings));
                    foreach (var setting in settings.Where(setting => setting.Value.Enabled))
                    {
                        try
                        {
                            var count = 0;
                            var cutoffDate = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(setting.Value.DaysToKeep));
                            if (setting.Value.EntirelyEraseInvoice)
                            {
                                await using var db = _dbContextFactory.CreateContext();
                                db.Invoices.RemoveRange(db.Invoices.Where(i => i.StoreDataId == setting.Key && i.Created < cutoffDate && (setting.Value.LastRunCutoff == null || i.Created > setting.Value.LastRunCutoff)));
                                count = await db.SaveChangesAsync(cts.Token);
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
                                    }, cts.Token);

                                    foreach (var invoice in invoices)
                                    {
                                        //replace all buyer info with "erased"
                                        var metadata = invoice.Metadata;
                                        if (!string.IsNullOrEmpty(metadata.BuyerAddress1) ||
                                            !string.IsNullOrEmpty(metadata.BuyerAddress2) ||
                                            !string.IsNullOrEmpty(metadata.BuyerCity) ||
                                            !string.IsNullOrEmpty(metadata.BuyerCountry) ||
                                            !string.IsNullOrEmpty(metadata.BuyerEmail) ||
                                            !string.IsNullOrEmpty(metadata.BuyerName) ||
                                            !string.IsNullOrEmpty(metadata.BuyerPhone) ||
                                            !string.IsNullOrEmpty(metadata.BuyerState) ||
                                            !string.IsNullOrEmpty(metadata.BuyerZip))
                                        {
                                            if (!string.IsNullOrEmpty(metadata.BuyerAddress1))
                                                metadata.BuyerAddress1 = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerAddress2))
                                                metadata.BuyerAddress2 = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerCity))
                                                metadata.BuyerCity = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerCountry))
                                                metadata.BuyerCountry = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerEmail))
                                                metadata.BuyerEmail = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerName))
                                                metadata.BuyerName = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerPhone))
                                                metadata.BuyerPhone = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerState))
                                                metadata.BuyerState = "erased";
                                            if (!string.IsNullOrEmpty(metadata.BuyerZip))
                                                metadata.BuyerZip = "erased";
                                            await _invoiceRepository.UpdateInvoiceMetadata(invoice.Id, invoice.StoreId, metadata.ToJObject());
                                        }
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
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Failed to erase data for store {StoreId}", setting.Key);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Data erasure cycle failed");
                }
                finally
                {
                    IsRunning = false;
                    _runningLock.Release();
                }

                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            cts.Dispose();
        }

        private CancellationTokenSource _cts;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cts = cts;
            _ = Run(cts);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
