using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Breez;

public class BreezLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly BreezService _breezService;

    public BreezLightningConnectionStringHandler(BreezService breezService)
    {
        _breezService = breezService;
    }
    public ILightningClient Create(string connectionString, Network network, out string error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "breez")
        {
            error = null;
            return null;
        }

        
        if (!kv.TryGetValue("key", out var key))
        {
            error = $"The key 'key' is mandatory for breez connection strings";
            return null;
        }
        
        error = null;
        return _breezService.GetClientByPaymentKey(key);
    }
}
