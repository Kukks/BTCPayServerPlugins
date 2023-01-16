using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.RockstarStylist
{
    public class RockstarStylistPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier { get; } = "BTCPayServer.Plugins.RockstarStylist";
        public override string Name { get; } = "Rockstar hairstylist";
        public override string Description { get; } = "Allows your checkout to get a rockstar approved makeover";

        public override void Execute(IServiceCollection services)
        {
            services.AddSingleton<IUIExtension>(new UIExtension("InvoiceCheckoutThemeOptions",
                "invoice-checkout-theme-options"));
            services.AddSingleton<RockstarStyleProvider>();
        }
        

        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new IBTCPayServerPlugin.PluginDependency() { Identifier = nameof(BTCPayServer), Condition = ">=1.4.6.0" }
        };
    }
}
