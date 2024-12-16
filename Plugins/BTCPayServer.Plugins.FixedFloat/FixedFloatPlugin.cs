using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.FixedFloat
{
    public class FixedFloatPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">2.0.4" }

        };
        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<FixedFloatService>();
            applicationBuilder.AddUIExtension("store-integrations-nav", "FixedFloat/FixedFloatNav");
            applicationBuilder.AddUIExtension("checkout-payment-method", "FixedFloat/CheckoutPaymentMethodExtension");
            applicationBuilder.AddUIExtension("checkout-payment", "FixedFloat/CheckoutPaymentExtension");

            base.Execute(applicationBuilder);
        }
    }

}
