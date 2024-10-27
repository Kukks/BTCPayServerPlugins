using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.DataErasure
{
    public class DataErasurePlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
        };
        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<DataErasureService>();
            applicationBuilder.AddHostedService( sp => sp.GetRequiredService<DataErasureService>());
            applicationBuilder.AddUIExtension("store-integrations-nav", "DataErasure/DataErasureNav");
            base.Execute(applicationBuilder);
        }
    }
}
