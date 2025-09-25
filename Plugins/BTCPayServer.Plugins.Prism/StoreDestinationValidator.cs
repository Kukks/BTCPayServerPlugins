using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using static System.String;

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

    private static (string storeId, PaymentMethodId paymentMethod) Parse(string destination)
    {
        if (destination is not string args1 || !args1.StartsWith("store-prism:", StringComparison.InvariantCultureIgnoreCase)) return (null, null);
        var parts = args1.Split(':', StringSplitOptions.RemoveEmptyEntries);
        string storeId = parts[1];
        string paymentMethod = parts.Length > 2 ? parts[2] : null;
        
        var payoutMethodId = (!IsNullOrEmpty(paymentMethod) && PaymentMethodId.TryParse(paymentMethod, out var parsedPmi)) ? parsedPmi : PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
       

        return (storeId, payoutMethodId);
        
    }
    
    
    public async Task<object> Execute(object args)
    {
        var result = new PrismDestinationValidationResult();
        result.Success = false;
        if (args is not string args1 || !args1.StartsWith("store-prism:", StringComparison.InvariantCultureIgnoreCase)) return args;
        try
        {
            var _payoutMethodHandlerDictionary = _serviceProvider.GetRequiredService<PayoutMethodHandlerDictionary>();
                
                var parsed = Parse(args1);

            var store = await _storeRepository.FindStore(parsed.storeId);
            if (store == null) return result;

            var blob = store.GetStoreBlob();
            if(!PayoutMethodId.TryParse(parsed.paymentMethod.ToString(), out var PM) || !_payoutMethodHandlerDictionary.TryGetValue(PM, out var handler)) return result;


            var config = store.GetPaymentMethodConfig(parsed.paymentMethod, _handlers, onlyEnabled: true);
            if (config == null || blob.GetExcludedPaymentMethods().Match(parsed.paymentMethod)) return result;

            result.Success = true;
            result.PayoutMethodId = PM;
            return result;
        }
        catch (Exception){ return result; }
    }
}
