using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LSP
{
    public class LSPPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier => "BTCPayServer.Plugins.LSP";
        public override string Name => "LSP";
        
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.6.0.0"}
        };

        public override string Description =>
            "Allows you to become an LSP selling lightning channels with inbound liquidity";

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<LSPService>();
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("LSP/StoreIntegrationLSPOption",
                "store-integrations-list"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("LSP/LSPNav",
                "store-integrations-nav"));
            base.Execute(applicationBuilder);
        }
    }
}
