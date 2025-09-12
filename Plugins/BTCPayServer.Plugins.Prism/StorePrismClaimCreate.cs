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
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

internal class StorePrismClaimCreate : IPluginHookFilter
{

    private readonly StoreRepository _storeRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly WalletReceiveService _walletReceiveService;
    public string Hook => "prism-claim-create";

    public StorePrismClaimCreate(IServiceProvider serviceProvider, StoreRepository storeRepository, 
        WalletReceiveService walletReceiveService, PaymentMethodHandlerDictionary handlers)
    {
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _walletReceiveService = walletReceiveService;
    }

    public async Task<object> Execute(object args)
    {
        if (args is not ClaimRequest claimRequest) return args;

        if (claimRequest.Destination?.ToString() is not { } args1 || !args1.StartsWith("store-prism:", StringComparison.OrdinalIgnoreCase))
            return args;

        try
        {
            var argBody = args1["store-prism:".Length..];
            var lastColon = argBody.LastIndexOf(':');
            var storeId = lastColon == -1 ? argBody : argBody[..lastColon];

            if (string.IsNullOrWhiteSpace(storeId)) return null;

            var store = await _storeRepository.FindStore(storeId);
            if (store == null) return null;

            if (!store.AnyPaymentMethodAvailable(_handlers)) return null;

            var walletId = new WalletId(store.Id, "BTC");
            var address = (await _walletReceiveService.GetOrGenerate(walletId)).Address?.ToString();
            if (string.IsNullOrWhiteSpace(address)) return null;

            var paymentMethod = PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
            _serviceProvider.GetService<PayoutMethodHandlerDictionary>().TryGetValue(paymentMethod, out var handler);
            if (handler is null) return null;

            var claimDestination = await handler.ParseClaimDestination(address, CancellationToken.None);
            if (claimDestination.destination is null) return null;

            claimRequest.Metadata = JObject.FromObject(new
            {
                Source = $"Prism - Store transfer",
                DestinationStore = store.Id
            });
            claimRequest.Destination = claimDestination.destination;
            claimRequest.PayoutMethodId = paymentMethod;
            return claimRequest;
        }
        catch (Exception) { return null; }
    }
}
