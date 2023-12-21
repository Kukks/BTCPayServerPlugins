using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.Prism;

public class LNURLPrismClaimCreate : IPluginHookFilter
{
    private readonly BTCPayNetworkProvider _networkProvider;
    public string Hook => "prism-claim-create";

    public LNURLPrismClaimCreate(BTCPayNetworkProvider networkProvider)
    {
        _networkProvider = networkProvider;
    }
    public async Task<object> Execute(object args)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (args is not ClaimRequest claimRequest || network is null)
        {
            return args;
        }
        
        if (claimRequest.Destination?.ToString() is not { } potentialLnurl) return args;
       
        try
        {
            LNURL.LNURL.ExtractUriFromInternetIdentifier(potentialLnurl);
            claimRequest.Destination = new LNURLPayClaimDestinaton(potentialLnurl);
            return claimRequest;
        }
        catch (Exception e)
        {
            try
            {
                LNURL.LNURL.Parse(potentialLnurl, out _);
                claimRequest.Destination = new LNURLPayClaimDestinaton(potentialLnurl);
                return claimRequest;
            }
            catch (Exception)
            {
                // ignored
            }
        }

        return args;
    }
}