#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Blink
{
   
    public class BlinkPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
            
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddUIExtension("ln-payment-method-setup-tab", "Blink/LNPaymentMethodSetupTab");
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BlinkLightningConnectionStringHandler>());
            applicationBuilder.AddSingleton<BlinkLightningConnectionStringHandler>();
            // Align served LNURL-pay metadata/bounds with Blink for non-custodial (ln-address) stores so
            // strict wallets accept the Blink-minted invoice (LUD-06 description-hash commitment).
            applicationBuilder.AddSingleton<Abstractions.Contracts.IPluginHookFilter, BlinkLnurlRequestFilter>();

            base.Execute(applicationBuilder);
        }
        
    }
}