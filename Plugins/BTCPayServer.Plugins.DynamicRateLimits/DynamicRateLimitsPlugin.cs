using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.DynamicRateLimits;

public class DynamicRateLimitsPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    };
    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<DynamicRateLimitsService>();
        applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DynamicRateLimitsService>());
        applicationBuilder.AddUIExtension("server-nav", "DynamicRateLimitsPlugin/Nav");
        base.Execute(applicationBuilder);
    }
}