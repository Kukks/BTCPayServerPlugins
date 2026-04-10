using BTCPayServer.Logging;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Electrum.Services;

public class ElectrumConnectionFactory : NBXplorerConnectionFactory
{
    public ElectrumConnectionFactory()
        : base(Microsoft.Extensions.Options.Options.Create(
            new BTCPayServer.Configuration.NBXplorerOptions()), new Logs())
    {
        Available = false;
    }
}
