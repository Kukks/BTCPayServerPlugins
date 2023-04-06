using System.Text;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.NIP05
{
    public class Nip05Plugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.9.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Nip05Nav",
                "store-integrations-nav"));
            applicationBuilder.AddSingleton<IPluginHookFilter, LnurlDescriptionFilter>();
            applicationBuilder.AddSingleton<IPluginHookFilter, LnurlFilter>();
            applicationBuilder.AddHostedService<Zapper>();
            base.Execute(applicationBuilder);
        }

        public static string GetZapRequestCacheKey(string invoiceid)
        {
            return nameof(Nip05Plugin)+ invoiceid;
        }
    }
}