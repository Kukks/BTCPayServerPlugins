using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Laraue.EfCoreTriggers.PostgreSql.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroNodePlugin:BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new () { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    };

    
    public override void Execute(IServiceCollection applicationBuilder)
    {
        
        applicationBuilder.AddUIExtension("header-nav", "MicroNode/MicroNodeNav");
        applicationBuilder.AddUIExtension("ln-payment-method-setup-tabhead", "MicroNode/LNPaymentMethodSetupTabhead");
        applicationBuilder.AddUIExtension("ln-payment-method-setup-tab", "MicroNode/LNPaymentMethodSetupTab");
        // applicationBuilder.AddStartupTask<MicroNodeStartupTask>();
        applicationBuilder.AddSingleton<MicroNodeContextFactory>();
        applicationBuilder.AddDbContext<MicroNodeContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<MicroNodeContextFactory>();
            factory.ConfigureBuilder(o);
            o.UsePostgreSqlTriggers();
        });
        applicationBuilder.AddSingleton<ILightningConnectionStringHandler, MicroLightningConnectionStringHandler>();
        applicationBuilder.AddSingleton<MicroNodeService>();
        applicationBuilder.AddHostedService(provider => provider.GetRequiredService<MicroNodeService>());
        
        
        base.Execute(applicationBuilder);
        
    }
}