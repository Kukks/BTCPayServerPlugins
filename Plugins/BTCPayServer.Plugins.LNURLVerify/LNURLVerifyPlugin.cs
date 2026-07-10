#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LNURLVerifyPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("ln-payment-method-setup-tab", "LNURLVerify/LNPaymentMethodSetupTab");
        services.AddSingleton<LNURLVerifyConnectionStringHandler>();
        services.AddSingleton<ILightningConnectionStringHandler>(sp =>
            sp.GetRequiredService<LNURLVerifyConnectionStringHandler>());
        // The shared verify poller (IHostedService) is registered in Task 5, once it exists.
        base.Execute(services);
    }
}
