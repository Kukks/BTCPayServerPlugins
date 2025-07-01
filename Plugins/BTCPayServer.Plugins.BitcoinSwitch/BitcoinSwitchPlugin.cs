using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.FileSeller;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BitcoinSwitch;

public class BitcoinSwitchPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<BitcoinSwitchService>();
        applicationBuilder.AddHostedService<BitcoinSwitchService>(provider => provider.GetRequiredService<BitcoinSwitchService>());
        applicationBuilder.AddUIExtension("app-template-editor-item-detail", "BitcoinSwitch/BitcoinSwitchPluginTemplateEditorItemDetail");

        base.Execute(applicationBuilder);
    }
}