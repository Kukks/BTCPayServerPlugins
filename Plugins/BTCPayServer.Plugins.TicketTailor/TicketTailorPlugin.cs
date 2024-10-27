using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.HostedServices.Webhooks;
using BTCPayServer.Services.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.TicketTailor
{
    public class TicketTailorPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddStartupTask<AppMigrate>();
            applicationBuilder.AddSingleton<TicketTailorService>();
            applicationBuilder.AddSingleton<IWebhookProvider>(o => o.GetRequiredService<TicketTailorService>());
            applicationBuilder.AddHostedService(s => s.GetRequiredService<TicketTailorService>());

            applicationBuilder.AddUIExtension("header-nav", "TicketTailor/NavExtension");
            applicationBuilder.AddSingleton<AppBaseType, TicketTailorApp>();
            base.Execute(applicationBuilder);
        }
    }
}