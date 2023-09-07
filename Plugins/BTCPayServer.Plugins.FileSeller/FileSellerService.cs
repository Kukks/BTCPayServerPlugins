using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Crowdfund;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Storage.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.FileSeller
{
    public class FileSellerService : EventHostedServiceBase
    {
        private readonly AppService _appService;
        private readonly FileService _fileService;
        private readonly InvoiceRepository _invoiceRepository;
        private readonly StoredFileRepository _storedFileRepository;

        public FileSellerService(EventAggregator eventAggregator,
            ILogger<FileSellerService> logger,
            AppService appService,
            FileService fileService,
            InvoiceRepository invoiceRepository,
            StoredFileRepository storedFileRepository) : base(eventAggregator, logger)
        {
            _appService = appService;
            _fileService = fileService;
            _invoiceRepository = invoiceRepository;
            _storedFileRepository = storedFileRepository;
        }

        public static Uri UrlToUse { get; set; }

        protected override void SubscribeToEvents()
        {
            Subscribe<InvoiceEvent>();
            base.SubscribeToEvents();
        }

        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            if (evt is not InvoiceEvent invoiceEvent) return;
            List<PosCartItem> cartItems = null;
            if (invoiceEvent.Name is not (InvoiceEvent.Completed or InvoiceEvent.MarkedCompleted
                or InvoiceEvent.Confirmed))
            {
                return;
            }

            var appIds = AppService.GetAppInternalTags(invoiceEvent.Invoice);

            if (!appIds.Any())
            {
                return;
            }

            if (invoiceEvent.Invoice.Metadata.AdditionalData.TryGetValue("fileselleractivated", out var activated))
            {
                return;
            }

            if ((!string.IsNullOrEmpty(invoiceEvent.Invoice.Metadata.ItemCode) ||
                 AppService.TryParsePosCartItems(invoiceEvent.Invoice.Metadata.PosData, out cartItems)))
            {
                var items = cartItems ?? new List<PosCartItem>();
                if (!string.IsNullOrEmpty(invoiceEvent.Invoice.Metadata.ItemCode) &&
                    !items.Exists(cartItem => cartItem.Id == invoiceEvent.Invoice.Metadata.ItemCode))
                {
                    items.Add(new PosCartItem()
                    {
                        Id = invoiceEvent.Invoice.Metadata.ItemCode,
                        Count = 1,
                        Price = invoiceEvent.Invoice.Price
                    });
                }

                var apps = (await _appService.GetApps(appIds)).Select(data =>
                {
                    switch (data.AppType)
                    {
                        case PointOfSaleAppType.AppType:
                            var possettings = data.GetSettings<PointOfSaleSettings>();
                            return (Data: data, Settings: (object) possettings,
                                Items: AppService.Parse(possettings.Template));
                        case CrowdfundAppType.AppType:
                            var cfsettings = data.GetSettings<CrowdfundSettings>();
                            return (Data: data, Settings: cfsettings,
                                Items: AppService.Parse(cfsettings.PerksTemplate));
                        default:
                            return (null, null, null);
                    }
                }).Where(tuple => tuple.Data != null && tuple.Items.Any(item =>
                    item.AdditionalData?.ContainsKey("file") is true &&
                    items.Exists(cartItem => cartItem.Id == item.Id)));

                var fileIds = new HashSet<string>();

                foreach (var valueTuple in apps)
                {
                    foreach (var item1 in valueTuple.Items.Where(item =>
                                 item.AdditionalData?.ContainsKey("file") is true &&
                                 items.Exists(cartItem => cartItem.Id == item.Id)))
                    {
                        var fileId = item1.AdditionalData["file"].Value<string>();
                        fileIds.Add(fileId);
                    }
                }

                var loadedFiles = await _storedFileRepository.GetFiles(new StoredFileRepository.FilesQuery()
                {
                    Id = fileIds.ToArray()
                });
                var productLinkTasks = loadedFiles.ToDictionary(file => file,
                    file => _fileService.GetTemporaryFileUrl(UrlToUse, file.Id, DateTimeOffset.MaxValue, true));

                var res = await Task.WhenAll(productLinkTasks.Values);


                if (res.Any(s => !string.IsNullOrEmpty(s)))
                {
                    var productTitleToFile = productLinkTasks.Select(pair => (pair.Key.FileName, pair.Value.Result))
                        .Where(s => s.Result is not null)
                        .ToDictionary(tuple => tuple.FileName, tuple => tuple.Result);

                    var receiptData = new JObject();
                    receiptData.Add("Downloadable Content", JObject.FromObject(productTitleToFile));

                    if (invoiceEvent.Invoice.Metadata.AdditionalData?.TryGetValue("receiptData",
                            out var existingReceiptData) is true &&
                        existingReceiptData is JObject existingReceiptDataObj)
                    {
                        receiptData.Merge(existingReceiptDataObj);
                    }

                    invoiceEvent.Invoice.Metadata.SetAdditionalData("receiptData", receiptData);
                }

                invoiceEvent.Invoice.Metadata.SetAdditionalData("fileselleractivated", "true");
                await _invoiceRepository.UpdateInvoiceMetadata(invoiceEvent.InvoiceId, invoiceEvent.Invoice.StoreId,
                    invoiceEvent.Invoice.Metadata.ToJObject());
            }

            await base.ProcessEvent(evt, cancellationToken);
        }
    }
}