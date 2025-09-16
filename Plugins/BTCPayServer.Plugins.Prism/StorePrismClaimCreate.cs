using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

internal class StorePrismClaimCreate : IPluginHookFilter
{

    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly WalletReceiveService _walletReceiveService;
    private readonly PayoutMethodHandlerDictionary _payoutMethodHandlerDictionary;
    public string Hook => "prism-claim-create";

    public StorePrismClaimCreate(StoreRepository storeRepository, WalletReceiveService walletReceiveService, PaymentMethodHandlerDictionary handlers, 
        PayoutMethodHandlerDictionary payoutMethodHandlerDictionary)
    {
        _handlers = handlers;
        _storeRepository = storeRepository;
        _walletReceiveService = walletReceiveService;
        _payoutMethodHandlerDictionary = payoutMethodHandlerDictionary;
    }

    public async Task<object> Execute(object args)
    {
        if (args is not ClaimRequest claimRequest) return args;

        if (claimRequest.Destination?.ToString() is not { } args1 || !args1.StartsWith("store-prism:", StringComparison.OrdinalIgnoreCase))
            return args;

        try
        {
            var argBody = args1.Split(':')[1];
            var lastColon = argBody.LastIndexOf(':');
            var storeId = lastColon == -1 ? argBody : argBody[..lastColon];
            var paymentMethod = lastColon == -1 ? null : argBody[(lastColon + 1)..];

            if (string.IsNullOrWhiteSpace(storeId)) return null;

            var store = await _storeRepository.FindStore(storeId);
            if (store == null) return null;

            if (!store.AnyPaymentMethodAvailable(_handlers)) return null;

            var pmi = string.IsNullOrEmpty(paymentMethod) || PayoutMethodId.TryParse(paymentMethod, out var pmi2) ? PayoutTypes.CHAIN.GetPayoutMethodId("BTC") : pmi2;
            if (!_payoutMethodHandlerDictionary.TryGetValue(pmi, out var handler)) return null;

            var walletId = new WalletId(store.Id, "BTC");
            var address = (await _walletReceiveService.GetOrGenerate(walletId)).Address?.ToString();
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
}
