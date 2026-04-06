using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces FeeProviderFactory. Returns ElectrumFeeProvider for all networks.
/// </summary>
public class ElectrumFeeProviderFactory : IFeeProviderFactory
{
    private readonly Dictionary<string, IFeeProvider> _providers = new();

    public ElectrumFeeProviderFactory(
        BTCPayNetworkProvider networkProvider,
        ElectrumFeeProvider feeProvider)
    {
        foreach (var network in networkProvider.GetAll().OfType<BTCPayNetwork>())
        {
            _providers[network.CryptoCode.ToUpperInvariant()] = feeProvider;
        }
    }

    public IFeeProvider CreateFeeProvider(BTCPayNetworkBase network)
    {
        if (_providers.TryGetValue(network.CryptoCode.ToUpperInvariant(), out var provider))
            return provider;
        throw new NotSupportedException($"No fee provider for {network.CryptoCode}");
    }
}
