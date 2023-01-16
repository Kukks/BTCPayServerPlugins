using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.FixedFloat
{
    public class NFCPlugin : BaseBTCPayServerPlugin
    {
        public override string Identifier => "BTCPayServer.Plugins.NFC";
        public override string Name => "LNURL NFC Support";


        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">1.7.1.0" }
        };

        public override string Description =>
            "Allows you to support contactless card payments over NFC and LNURL Withdraw!";

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("NFC/CheckoutEnd",
                "checkout-end"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("NFC/LightningCheckoutPostContent",
                "checkout-lightning-post-content"));
            base.Execute(applicationBuilder);
        }
    }
}
