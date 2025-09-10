using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Prism.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Prism;

public class PrismPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.6"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {   
        applicationBuilder.AddServerSideBlazor(o => o.DetailedErrors = true);
        
        applicationBuilder.AddUIExtension("store-integrations-nav", "PrismNav");
        applicationBuilder.AddSingleton<SatBreaker>();
        applicationBuilder.AddHostedService(provider => provider.GetRequiredService<SatBreaker>());
        applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetService<AutoTransferService>());
        applicationBuilder.AddScheduledTask<AutoTransferService>(TimeSpan.FromHours(12));
        applicationBuilder.AddSingleton<IPluginHookFilter, StoreDestinationValidator>();
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