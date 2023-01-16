using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.AOPP
{
    public class AOPPPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier => "BTCPayServer.Plugins.AOPP";
        public override string Name => "AOPP";


        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=1.6.0.0" }
        };

        public override string Description =>
            "Allows you to support the AOPP protocol in invoices to allow customers to bypass stupid KYC rules.";

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<AOPPService>();
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/StoreIntegrationAOPPOption",
                "store-integrations-list"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/CheckoutContentExtension",
                "checkout-bitcoin-post-content"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/CheckoutContentExtension",
                "checkout-lightning-post-content"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/CheckoutTabExtension",
                "checkout-bitcoin-post-tabs"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/CheckoutTabExtension",
                "checkout-lightning-post-tabs"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/CheckoutEnd",
                "checkout-end"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("AOPP/AOPPNav",
                "store-integrations-nav"));
            base.Execute(applicationBuilder);
        }
    }
}
