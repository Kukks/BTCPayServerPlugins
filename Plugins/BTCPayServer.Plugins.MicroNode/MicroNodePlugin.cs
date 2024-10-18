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
        
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("MicroNode/MicroNodeNav", "header-nav"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("MicroNode/LNPaymentMethodSetupTabhead", "ln-payment-method-setup-tabhead"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("MicroNode/LNPaymentMethodSetupTab", "ln-payment-method-setup-tab"));
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