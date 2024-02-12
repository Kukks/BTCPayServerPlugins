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
        new() {Identifier = nameof(BTCPayServer), Condition = ">=1.12.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<BringinService>();
        applicationBuilder.AddSingleton<IHostedService, BringinService>(s => s.GetService<BringinService>());
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Bringin/BringinDashboardWidget", "dashboard"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Bringin/Nav",
            "store-integrations-nav"));
    }
}