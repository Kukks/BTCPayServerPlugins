using System;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BTCPayNetworkProvider _networkProvider;

    public MicroLightningConnectionStringHandler(
        IServiceProvider serviceProvider,
        BTCPayNetworkProvider networkProvider)
    {
        _serviceProvider = serviceProvider;
        _networkProvider = networkProvider;
    }

    public ILightningClient Create(string connectionString, Network network, out string error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "micro" || kv is null)
        {
            error = null;
            return null;
        }

        if (_networkProvider.BTC.NBitcoinNetwork != network)
        {
            error = "Invalid network";
            return null;
        }

        if (!kv.TryGetValue("key", out var key))
        {
            error = "key is missing";
            return null;
        }
        var microNodeService = _serviceProvider.GetService<MicroNodeService>();

        var settings = microNodeService.GetMasterSettingsFromKey(key).GetAwaiter().GetResult();

        if (settings is null)
        {
            error = "key is invalid";
            return null;
        }

        if (settings.Value.Item1.Enabled is not true)
        {
            error = "MicroBank is not enabled";
            return null;
        }

        var lightningClient = microNodeService.GetMasterLightningClient(settings.Value.Item2).GetAwaiter().GetResult();
        if (lightningClient is null)
        {
            error = "Lightning node not available";
            return null;
        }

        error = null;
        return new MicroLightningClient(lightningClient, microNodeService, network, key);
    }
}