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
            var existing = await Get(storeId);
            settings.LastRunCutoff = clearDate ? null : existing?.LastRunCutoff;
            await SetCore(storeId, settings);
            // Wake the single worker so new settings apply promptly; coalesced to one pending run.
            try { _wake.Release(); }
            catch (SemaphoreFullException) { }
        }

        private async Task SetCore(string storeId, DataErasureSettings settings)
        {
            await _storeRepository.UpdateSetting(storeId, nameof(DataErasureSettings), settings);
        }

        public bool IsRunning { get; private set; }

        // Signals the worker to run a cycle immediately, bounded to a single pending wake.
        private readonly SemaphoreSlim _wake = new(0, 1);

        private async Task RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
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
                                count = await db.SaveChangesAsync(ct);
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
                                    }, ct);

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
                            // Persist only cutoff progress against the latest settings, so a concurrent
                            // user change (e.g. disabling erasure mid-cycle) is not clobbered.
                            var latest = await Get(setting.Key);
                            if (latest != null)
                            {
                                latest.LastRunCutoff = cutoffDate;
                                await SetCore(setting.Key, latest);
                            }
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
                }

                try
                {
                    await _wake.WaitAsync(TimeSpan.FromHours(1), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private CancellationTokenSource _cts;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = RunLoop(_cts.Token);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cts?.Cancel();
            return Task.CompletedTask;
        }
    }
}
