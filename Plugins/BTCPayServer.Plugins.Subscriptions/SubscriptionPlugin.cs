using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.HostedServices.Webhooks;
using BTCPayServer.Services.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Subscriptions
{
    public class SubscriptionPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.13.0"}
        ];

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<SubscriptionService>();
            applicationBuilder.AddSingleton<IWebhookProvider>(o => o.GetRequiredService<SubscriptionService>());
            applicationBuilder.AddHostedService(s => s.GetRequiredService<SubscriptionService>());

            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Subscriptions/NavExtension", "header-nav"));
            applicationBuilder.AddSingleton<AppBaseType, SubscriptionApp>();
            base.Execute(applicationBuilder);
        }
    }
}