using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Prism;

public class PrismPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=2.3.2"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {   
        applicationBuilder.AddServerSideBlazor(o => o.DetailedErrors = true);
        
        applicationBuilder.AddUIExtension("store-integrations-nav", "PrismNav");
        applicationBuilder.AddSingleton<SatBreaker>();
        applicationBuilder.AddHostedService(provider => provider.GetRequiredService<SatBreaker>());
        applicationBuilder.AddScheduledTask<SatBreaker>(TimeSpan.FromHours(12));
        applicationBuilder.AddSingleton<StoreDestinationValidator>();
        applicationBuilder.AddSingleton<IPluginHookFilter>(provider => provider.GetRequiredService<StoreDestinationValidator>());
        applicationBuilder.AddSingleton<IPluginHookFilter, OpenSatsDestinationValidator>();
        applicationBuilder.AddSingleton<IPluginHookFilter, LNURLPrismDestinationValidator>();
        applicationBuilder.AddSingleton<IPluginHookFilter, OnChainPrismDestinationValidator>();
        applicationBuilder.AddSingleton<IPluginHookFilter, StorePrismClaimCreate>();
        applicationBuilder.AddSingleton<IPluginHookFilter, LNURLPrismClaimCreate>();
        applicationBuilder.AddSingleton<IPluginHookFilter, OnChainPrismClaimCreate>();
        applicationBuilder.AddSingleton<IPluginHookFilter, OpenSatsPrismClaimCreate>();
        base.Execute(applicationBuilder);
    }

    public override void Execute(IApplicationBuilder applicationBuilder, IServiceProvider applicationBuilderApplicationServices)
    {
      
    }
}