using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism;

internal class StorePrismClaimCreate : IPluginHookFilter
{
    private readonly StoreRepository _storeRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly WalletReceiveService _walletReceiveService;
    private readonly LightningAddressService _lightningAddressService;
    public string Hook => "prism-claim-create";

    public StorePrismClaimCreate(StoreRepository storeRepository, WalletReceiveService walletReceiveService, PaymentMethodHandlerDictionary handlers,
       IServiceProvider serviceProvider, LightningAddressService lightningAddressService, IHttpContextAccessor contextAccessor)
    {
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _walletReceiveService = walletReceiveService;
        _lightningAddressService = lightningAddressService;
        _contextAccessor = contextAccessor;
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

            if (!store.AnyPaymentMethodAvailable(_handlers)) return null;

            var pmi = string.IsNullOrEmpty(paymentMethod) || PayoutMethodId.TryParse(paymentMethod, out var pmi2) ? PayoutTypes.CHAIN.GetPayoutMethodId("BTC") : pmi2;
            if (!_payoutMethodHandlerDictionary.TryGetValue(pmi, out var handler)) return null;

            string? address = pmi switch
            {
                var id when id == PayoutTypes.CHAIN.GetPayoutMethodId("BTC")
                    => (await _walletReceiveService.GetOrGenerate(new WalletId(store.Id, "BTC"))).Address?.ToString(),

                var id when id == PayoutTypes.LN.GetPayoutMethodId("BTC")
                    => (await CreateLNUrlRequestFromStore(store)),

                _ => null
            };
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


    private async Task<string> CreateLNUrlRequestFromStore(StoreData store)
    {
        var addresses = await _lightningAddressService.Get(new LightningAddressQuery() { StoreIds = new[] { store.Id } });
        if (!addresses.Any()) return null;

        return $"{addresses.First().Username}@{_contextAccessor.HttpContext?.Request.Host.ToUriComponent()}";
    }

}
