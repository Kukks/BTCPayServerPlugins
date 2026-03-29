using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.ServerMessage;

public class ServerMessagePlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        // Admin settings page in Server Settings nav
        services.AddUIExtension("server-nav", "ServerMessage/Nav");

        // Admin banner on all authenticated backend pages
        services.AddUIExtension("header-nav", "ServerMessage/AdminBanner");

        // Public banners on customer-facing pages
        services.AddUIExtension("checkout-end", "ServerMessage/PublicBanner");
        services.AddUIExtension("pos-header", "ServerMessage/PublicBanner");
        services.AddUIExtension("pullpayment-foot", "ServerMessage/PublicBanner");
        services.AddUIExtension("crowdfund-head", "ServerMessage/PublicBanner");

        base.Execute(services);
    }
}
