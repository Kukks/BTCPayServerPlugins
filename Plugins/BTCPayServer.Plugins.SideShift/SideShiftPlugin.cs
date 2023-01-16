using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SideShift
{
    public class SideShiftPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier => "BTCPayServer.Plugins.SideShift";
        public override string Name => "SideShift";


        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new IBTCPayServerPlugin.PluginDependency() { Identifier = nameof(BTCPayServer), Condition = ">=1.7.0.0" }
        };

        public override string Description =>
            "Allows you to embed a SideShift conversion screen to allow customers to pay with altcoins.";

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<SideShiftService>();
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/SideShiftNav",
                "store-integrations-nav"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/StoreIntegrationSideShiftOption",
                "store-integrations-list"));
            // Checkout v2
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutPaymentMethodExtension",
                "checkout-payment-method"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutPaymentExtension",
                "checkout-payment"));
            // Checkout Classic
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutContentExtension",
                "checkout-bitcoin-post-content"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutContentExtension",
                "checkout-lightning-post-content"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutTabExtension",
                "checkout-bitcoin-post-tabs"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutTabExtension",
                "checkout-lightning-post-tabs"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("SideShift/CheckoutEnd",
                "checkout-end"));
            base.Execute(applicationBuilder);
        }
    }
}
