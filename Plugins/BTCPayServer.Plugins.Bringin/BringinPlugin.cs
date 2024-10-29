using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Bringin;

public class BringinPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new()
        {
            Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"
        }
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<BringinService>();
        applicationBuilder.AddSingleton<IHostedService, BringinService>(s => s.GetService<BringinService>());
        applicationBuilder.AddUIExtension("dashboard", "Bringin/BringinDashboardWidget");
        applicationBuilder.AddUIExtension("store-integrations-nav", "Bringin/Nav");
    }
}