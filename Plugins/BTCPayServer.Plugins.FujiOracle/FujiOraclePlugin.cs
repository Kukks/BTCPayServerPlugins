using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.FujiOracle
{
    public class FujiOraclePlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier => "BTCPayServer.Plugins.FujiOracle";
        public override string Name => "Fuji Oracle";


        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=1.6.0.0" }
        };

        public override string Description =>
            "Allows you to become an oracle for the fuji.money platform";

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<FujiOracleService>();
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("FujiOracle/StoreIntegrationFujiOracleOption",
                "store-integrations-list"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("FujiOracle/FujiOracleNav",
                "store-integrations-nav"));
            base.Execute(applicationBuilder);
        }
    }
}
