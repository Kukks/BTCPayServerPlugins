using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Services.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Conference;

public class ConferencePlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<AppBaseType, ConferenceApp>();
        applicationBuilder.AddSingleton<ConferenceProvisioningService>();
        applicationBuilder.AddSingleton<ConferenceReportService>();
        applicationBuilder.AddSingleton<ConferenceCsvService>();
        applicationBuilder.AddUIExtension("header-nav", "Conference/NavExtension");
        base.Execute(applicationBuilder);
    }
}
