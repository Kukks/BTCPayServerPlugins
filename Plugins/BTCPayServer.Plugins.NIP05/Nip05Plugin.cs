using System.Text;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NNostr.Client;

namespace BTCPayServer.Plugins.NIP05
{
    public class Nip05Plugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Nip05Nav",
                "store-integrations-nav"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("NWC/LNPaymentMethodSetupTab", "ln-payment-method-setup-tab"));

            applicationBuilder.AddSingleton<IPluginHookFilter, LnurlDescriptionFilter>();
            applicationBuilder.AddSingleton<IPluginHookFilter, LnurlFilter>();
            applicationBuilder.TryAddSingleton<NostrClientPool>();
            applicationBuilder.AddSingleton<Zapper>();
            applicationBuilder.AddHostedService(sp => sp.GetRequiredService<Zapper>());
            applicationBuilder.AddSingleton<NostrWalletConnectLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<NostrWalletConnectLightningConnectionStringHandler>());

            base.Execute(applicationBuilder);
        }

        public static string GetZapRequestCacheKey(string invoiceid)
        {
            return nameof(Nip05Plugin)+ invoiceid;
        }
    }
}