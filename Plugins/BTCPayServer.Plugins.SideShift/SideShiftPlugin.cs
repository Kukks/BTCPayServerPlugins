using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SideShift
{
    public class SideShiftPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }

        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<SideShiftService>();
            applicationBuilder.AddHostedService(provider => provider.GetService<SideShiftService>());

            applicationBuilder.AddUIExtension("store-integrations-nav","SideShift/SideShiftNav");
            applicationBuilder.AddUIExtension("pullpayment-foot","SideShift/PullPaymentViewInsert");
            applicationBuilder.AddUIExtension("store-integrations-list", "SideShift/StoreIntegrationSideShiftOption");
            // Checkout v2
            applicationBuilder.AddUIExtension("checkout-payment-method", "SideShift/CheckoutPaymentMethodExtension");
            applicationBuilder.AddUIExtension("checkout-payment","SideShift/CheckoutPaymentExtension");
           
            base.Execute(applicationBuilder);
        }
    }
}