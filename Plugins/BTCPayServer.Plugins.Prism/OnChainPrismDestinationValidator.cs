using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.Prism;

public class OnChainPrismDestinationValidator : IPluginHookFilter
{
    private readonly BTCPayNetworkProvider _networkProvider;
    public string Hook => "prism-destination-validate";

    public OnChainPrismDestinationValidator(BTCPayNetworkProvider networkProvider)
    {
        _networkProvider = networkProvider;
    }

    public Task<object> Execute(object args)
    {
        if (args is not string args1) return Task.FromResult(args);
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
        {
            return Task.FromResult(args);
        }

        try
        {
            
            BitcoinAddress.Create(args1, network.NBitcoinNetwork);
            return Task.FromResult<object>(new PrismDestinationValidationResult()
            {
                Success = true,
                PaymentMethod = new PaymentMethodId("BTC", PaymentTypes.BTCLike)
            });
        }
        catch (Exception e)
        {
            try
            {
                var parser = new DerivationSchemeParser(network);
                var dsb = parser.Parse(args1);
                return Task.FromResult<object>(new PrismDestinationValidationResult()
                {
                    Success = true,
                    PaymentMethod = new PaymentMethodId("BTC", PaymentTypes.BTCLike)
                });
            }
            catch (Exception)
            {
            }
        }

        return Task.FromResult(args);
    }
}