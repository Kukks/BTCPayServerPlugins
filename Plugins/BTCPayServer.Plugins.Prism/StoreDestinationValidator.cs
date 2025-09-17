using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Prism;

public class StoreDestinationValidator : IPluginHookFilter
{
    private readonly StoreRepository _storeRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaymentMethodHandlerDictionary _handlers;
    public string Hook => "prism-destination-validate";

    public StoreDestinationValidator(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers, IServiceProvider serviceProvider)
    {
        _handlers = handlers;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
    }

    public async Task<object> Execute(object args)
    {
        var result = new PrismDestinationValidationResult();
        result.Success = false;
        if (args is not string args1 || !args1.StartsWith("store-prism:", StringComparison.InvariantCultureIgnoreCase)) return args;
        try
        {
            var _payoutMethodHandlerDictionary = _serviceProvider.GetRequiredService<PayoutMethodHandlerDictionary>();
            var parts = args1.Split(':', StringSplitOptions.RemoveEmptyEntries);
            string storeId = parts[1];
            string paymentMethod = parts.Length > 2 ? parts[2] : null;

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
