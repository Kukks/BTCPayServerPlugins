using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Prism;

public class PrismPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=1.10.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {   
        applicationBuilder.AddServerSideBlazor(o => o.DetailedErrors = true);
        
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("PrismNav",
            "store-integrations-nav"));
        applicationBuilder.AddSingleton<SatBreaker>();
        applicationBuilder.AddHostedService(provider => provider.GetRequiredService<SatBreaker>());
        base.Execute(applicationBuilder);
    }
}