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
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<BreezService>();
            applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BreezService>());
            applicationBuilder.AddSingleton<BreezLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BreezLightningConnectionStringHandler>());
            applicationBuilder.AddUIExtension("store-integrations-nav", "Breez/BreezNav");
            
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tabhead", "Breez/LNPaymentMethodSetupTabhead");
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tab", "Breez/LNPaymentMethodSetupTab");

            applicationBuilder.AddUIExtension("dashboard", "Breez/BreezNodeInfo");
            applicationBuilder.AddUIExtension("dashboard", "Breez/BreezPaymentsTable");
            base.Execute(applicationBuilder);
        }
    }
}