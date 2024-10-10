using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;

namespace BTCPayServer.Plugins.Prism;

public class LNURLPrismDestinationValidator : IPluginHookFilter
{
    public string Hook => "prism-destination-validate";

    public Task<object> Execute(object args)
    {
        if (args is not string args1) return Task.FromResult(args);

        try
        {
            LNURL.LNURL.ExtractUriFromInternetIdentifier(args1);
            return Task.FromResult<object>(new PrismDestinationValidationResult()
            {
                Success = true,
                PayoutMethodId = PayoutTypes.LN.GetPayoutMethodId("BTC")
            });
        }
        catch (Exception e)
        {
            try
            {
                LNURL.LNURL.Parse(args1, out var tag);
                return Task.FromResult<object>(new PrismDestinationValidationResult()
                {
                    Success = true,
                    PayoutMethodId =PayoutTypes.LN.GetPayoutMethodId("BTC")
                });
            }
            catch (Exception)
            {
            }
        }

        return Task.FromResult(args);
    }
}

public class PrismDestinationValidationResult
{
    public bool Success { get; set; }
    public PayoutMethodId PayoutMethodId { get; set; }
}