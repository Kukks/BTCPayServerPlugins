using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.Prism;

public class OnChainPrismClaimCreate : IPluginHookFilter
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly ILogger<OnChainPrismClaimCreate> _logger;
    public string Hook => "prism-claim-create";

    public OnChainPrismClaimCreate(BTCPayNetworkProvider networkProvider, ExplorerClientProvider explorerClientProvider, ILogger<OnChainPrismClaimCreate> logger)
    {
        _networkProvider = networkProvider;
        _explorerClientProvider = explorerClientProvider;
        _logger = logger;
    }

    public async Task<object> Execute(object args)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (args is not ClaimRequest claimRequest || network is null)
        {
            return args;
        }

        if (claimRequest.Destination?.Id is not { } destStr) return args;
        try
        {
            claimRequest.Destination =
                new AddressClaimDestination(BitcoinAddress.Create(destStr, network.NBitcoinNetwork));
            claimRequest.PayoutMethodId = PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
            return args;
        }
        catch (Exception)
        {
            try
            {
                var ds = new DerivationSchemeParser(network).Parse(destStr);
                var ec = _explorerClientProvider.GetExplorerClient(network);
                var add = await ec.GetUnusedAsync(ds, DerivationFeature.Deposit, 0, true);

                claimRequest.Destination =
                    new AddressClaimDestination(add.Address);
                claimRequest.PayoutMethodId = PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse {Destination} as on-chain address or derivation scheme", destStr);
            }
        }


        return args;
    }
}