using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Terminal.Services;
using BTCPayServer.Services.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Terminal;

public class TerminalPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<AppBaseType, TerminalApp>();
        services.AddSingleton<TerminalService>();
        services.AddHostedService<TerminalInvoiceInterceptor>();
        services.AddUIExtension("header-nav", "Terminal/NavExtension");
        base.Execute(services);
    }
}
