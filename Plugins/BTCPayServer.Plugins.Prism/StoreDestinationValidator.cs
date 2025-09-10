using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Prism;

public class StoreDestinationValidator : IPluginHookFilter
{
    private readonly StoreRepository _storeRepository;
    public string Hook => "prism-destination-validate";

    public StoreDestinationValidator(StoreRepository storeRepository)
    {
        _storeRepository = storeRepository;
    }

    public async Task<object> Execute(object args)
    {
        var result = new PrismDestinationValidationResult();
        result.Success = false;
        if (args is not string args1 || !args1.StartsWith("store-prism:", StringComparison.InvariantCultureIgnoreCase)) return args;

        try
        {
            var storeId = args1["store-prism:".Length..];
            if (string.IsNullOrWhiteSpace(storeId)) return result;

            var store = await _storeRepository.FindStore(storeId);
            if (store == null) return result;

            result.Success = true;
            result.PayoutMethodId = PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
            return result;
        }
        catch (Exception){ return result; }
    }
}
