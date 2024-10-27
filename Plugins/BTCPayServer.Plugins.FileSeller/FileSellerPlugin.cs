using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.FileSeller;

public class FileSellerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddHostedService<FileSellerService>();
        applicationBuilder.AddUIExtension("header-nav", "FileSeller/Detect");
        applicationBuilder.AddUIExtension("checkout-end", "FileSeller/Detect");
        applicationBuilder.AddUIExtension("app-template-editor-item-detail", "FileSeller/FileSellerTemplateEditorItemDetail");

        base.Execute(applicationBuilder);
    }
}