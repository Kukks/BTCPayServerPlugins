using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.DynamicReports;

public class DynamicReportsPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    };
    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<DynamicReportService>();
        applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DynamicReportService>());
        applicationBuilder.AddUIExtension("server-nav", "DynamicReportsPlugin/Nav");
        base.Execute(applicationBuilder);
    }
}