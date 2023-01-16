using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.TicketTailor
{
    public class TicketTailorPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier => "BTCPayServer.Plugins.TicketTailor";
        public override string Name => "TicketTailor";


        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=1.6.0.0" }
        };

        public override string Description =>
            "Allows you to integrate with TicketTailor.com to sell tickets for Bitcoin";

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<TicketTailorService>();
            applicationBuilder.AddHostedService(s=>s.GetRequiredService<TicketTailorService>());
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("TicketTailor/StoreIntegrationTicketTailorOption",
                "store-integrations-list"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("TicketTailor/TicketTailorNav",
                "store-integrations-nav"));
            base.Execute(applicationBuilder);
        }
    }
}
