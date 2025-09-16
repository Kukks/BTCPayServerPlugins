using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Prism;

public class StoreDestinationValidator : IPluginHookFilter
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly PayoutMethodHandlerDictionary _payoutMethodHandlerDictionary;
    public string Hook => "prism-destination-validate";

    public StoreDestinationValidator(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers,
        PayoutMethodHandlerDictionary payoutMethodHandlerDictionary)
    {
        _handlers = handlers;
        _storeRepository = storeRepository;
        _payoutMethodHandlerDictionary = payoutMethodHandlerDictionary;
    }

    public async Task<object> Execute(object args)
    {
        var result = new PrismDestinationValidationResult();
        result.Success = false;
        if (args is not string args1 || !args1.StartsWith("store-prism:", StringComparison.InvariantCultureIgnoreCase)) return args;

        try
        {
            var argBody = args1.Split(':')[1];
            var lastColon = argBody.LastIndexOf(':');
            var storeId = lastColon == -1 ? argBody : argBody[..lastColon];
            var paymentMethod = lastColon == -1 ? null : argBody[(lastColon + 1)..];

            if (string.IsNullOrWhiteSpace(storeId)) return result;

            var store = await _storeRepository.FindStore(storeId);
            if (store == null) return result;

            if (!store.AnyPaymentMethodAvailable(_handlers)) return result;

            var pmi = string.IsNullOrEmpty(paymentMethod) || PayoutMethodId.TryParse(paymentMethod, out var pmi2) ? PayoutTypes.CHAIN.GetPayoutMethodId("BTC") : pmi2;
            if (!_payoutMethodHandlerDictionary.TryGetValue(pmi, out var handler)) return result;

            result.Success = true;
            result.PayoutMethodId = pmi;
            return result;
        }
        catch (Exception){ return result; }
    }
}
