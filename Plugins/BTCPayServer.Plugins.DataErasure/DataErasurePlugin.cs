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
            new() { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
        };
        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<DataErasureService>();
            applicationBuilder.AddHostedService( sp => sp.GetRequiredService<DataErasureService>());
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("DataErasure/DataErasureNav",
                "store-integrations-nav"));
            base.Execute(applicationBuilder);
        }
    }
}
