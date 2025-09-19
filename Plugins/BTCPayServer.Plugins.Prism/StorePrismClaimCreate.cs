using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

internal class StorePrismClaimCreate : IPluginHookFilter
{
    private readonly StoreRepository _storeRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    public string Hook => "prism-claim-create";

    public StorePrismClaimCreate(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers, IServiceProvider serviceProvider)
    {
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
    }

    public async Task<object> Execute(object args)
    {
        if (args is not ClaimRequest claimRequest) return args;

        if (claimRequest.Destination?.ToString() is not { } args1 || !args1.StartsWith("store-prism:", StringComparison.OrdinalIgnoreCase))
            return args;
        try
        {
            var _payoutMethodHandlerDictionary = _serviceProvider.GetRequiredService<PayoutMethodHandlerDictionary>();
            var parts = args1.Split(':', StringSplitOptions.RemoveEmptyEntries);
            string storeId = parts[1];
            string paymentMethod = parts.Length > 2 ? parts[2] : null;

            if (string.IsNullOrWhiteSpace(storeId)) return null;

            var store = await _storeRepository.FindStore(storeId);
            if (store == null) return null;

            var pmi = (!string.IsNullOrEmpty(paymentMethod) && PayoutMethodId.TryParse(paymentMethod, out var parsedPmi)) ? parsedPmi : PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
            if (!_payoutMethodHandlerDictionary.TryGetValue(pmi, out var handler)) return null;

            var address = await CreateClaimRequestDestination(store, pmi);
            if (string.IsNullOrWhiteSpace(address)) return null;

            var claimDestination = await handler.ParseClaimDestination(address, CancellationToken.None);
            if (claimDestination.destination is null) return null;

            claimRequest.Metadata = JObject.FromObject(new
            {
                Source = $"Prism - Store transfer",
                DestinationStore = store.Id
            });
            claimRequest.Destination = claimDestination.destination;
            claimRequest.PayoutMethodId = pmi;
            return claimRequest;
        }
        catch (Exception) { return null; }
    }


    private async Task<string> CreateClaimRequestDestination(Data.StoreData store, PayoutMethodId payoutMethodId)
    {
        var blob = store.GetStoreBlob();
        var invoiceController = _serviceProvider.GetRequiredService<UIInvoiceController>();
        PaymentMethodId? pmi = payoutMethodId switch
        {
            var id when id == PayoutTypes.LN.GetPayoutMethodId("BTC") => PaymentTypes.LNURL.GetPaymentMethodId("BTC"),

            var id when id == PayoutTypes.CHAIN.GetPayoutMethodId("BTC") => PaymentTypes.CHAIN.GetPaymentMethodId("BTC"),

            _ => null
        };
        if (pmi is null) return null;

        var config = store.GetPaymentMethodConfig(pmi, _handlers, onlyEnabled: true);
        if (config == null || blob.GetExcludedPaymentMethods().Match(pmi)) return null;

        InvoiceEntity invoice = await invoiceController.CreateInvoiceCoreRaw(new CreateInvoiceRequest
        {
            Currency = "SATS",
            Checkout = new InvoiceDataBase.CheckoutOptions { LazyPaymentMethods = false, PaymentMethods = new[] { pmi.ToString() }, Expiration = TimeSpan.FromDays(60) },
        }, store, null);
        return invoice.GetPaymentPrompt(pmi)?.Destination;
    }
}
