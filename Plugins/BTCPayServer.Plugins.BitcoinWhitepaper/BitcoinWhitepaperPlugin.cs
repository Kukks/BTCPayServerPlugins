using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;

namespace BTCPayServer.Plugins.BitcoinWhitepaper
{
    public class BitcoinWhitepaperPlugin: BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
            {
                new() { Identifier = nameof(BTCPayServer), Condition = ">=1.7.4" }
            };
    }
}
