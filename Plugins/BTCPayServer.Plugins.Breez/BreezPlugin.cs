#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Breez
{
    public class BreezPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.13.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<BreezService>();
            applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BreezService>());
            applicationBuilder.AddSingleton<BreezLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BreezLightningConnectionStringHandler>());
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/BreezNav",
                "store-integrations-nav"));
            
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/LNPaymentMethodSetupTabhead", "ln-payment-method-setup-tabhead"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/LNPaymentMethodSetupTab", "ln-payment-method-setup-tab"));

            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/BreezNodeInfo",
                "dashboard"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/BreezPaymentsTable",
                "dashboard"));
            base.Execute(applicationBuilder);
        }
    }
}