using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.FileSeller;

public class FileSellerPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
    };
    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddHostedService<FileSellerService>();
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("FileSeller/Detect",
            "header-nav"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("FileSeller/Detect",
            "checkout-end"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("FileSeller/FileSellerTemplateEditorItemDetail",
            "app-template-editor-item-detail"));
        

        base.Execute(applicationBuilder);
    }
}