using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

internal class StorePrismClaimCreate : IPluginHookFilter
{
    private readonly StoreRepository _storeRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<StorePrismClaimCreate> _logger;
    public string Hook => "prism-claim-create";

    public StorePrismClaimCreate(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers,
        IServiceProvider serviceProvider, BTCPayNetworkProvider networkProvider, ILogger<StorePrismClaimCreate> logger)
    {
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _networkProvider = networkProvider;
        _logger = logger;
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
            string destinationId = parts[1];
            string paymentMethod = parts.Length > 2 ? parts[2] : null;

            if (string.IsNullOrWhiteSpace(destinationId)) return null;

            var pmi = (!string.IsNullOrEmpty(paymentMethod) && PayoutMethodId.TryParse(paymentMethod, out var parsedPmi)) ? parsedPmi : PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
            if (!_payoutMethodHandlerDictionary.TryGetValue(pmi, out var handler)) return null;

            var store = await _storeRepository.FindStore(destinationId);
            if (store != null)
            {
                var address = await CreateClaimRequestDestination(store, pmi);
                if (string.IsNullOrWhiteSpace(address)) return null;

                var claimDestination = await handler.ParseClaimDestination(address, CancellationToken.None);
                if (claimDestination.destination is null) return null;

                claimRequest.Destination = claimDestination.destination;
            }
            else
            {
                if (!TryParseDestinationAddress(destinationId, claimRequest)) return null;
            }
            claimRequest.Metadata = JObject.FromObject(new
            {
                Source = $"Prism - Store transfer",
                Destination = destinationId
            });
            claimRequest.PayoutMethodId = pmi;
            return claimRequest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create store-prism claim for destination {Destination}", args1);
            return null;
        }
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

    private bool TryParseDestinationAddress(string address, ClaimRequest claimRequest)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null) return false;

        try
        {
            claimRequest.Destination = new AddressClaimDestination(BitcoinAddress.Create(address, network.NBitcoinNetwork));
            return true;
        }
        catch { }

        try
        {
            LNURL.LNURL.ExtractUriFromInternetIdentifier(address);
            claimRequest.Destination = new LNURLPayClaimDestinaton(address);
            return true;
        }
        catch { }

        try
        {
            LNURL.LNURL.Parse(address, out _);
            claimRequest.Destination = new LNURLPayClaimDestinaton(address);
            return true;
        }
        catch { }

        return false;
    }
}
