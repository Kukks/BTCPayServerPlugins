#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SwapMiddleware.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SwapMiddleware;

public class SwapMiddlewarePlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // Register the middleware service as singleton
        services.AddSingleton<SwapMiddlewareService>();

        // Register named HTTP clients for swap providers
        services.AddHttpClient("sideshift-proxy");
        services.AddHttpClient("fixedfloat-proxy");

        // Add server-level nav extension (appears in Server Settings)
        services.AddUIExtension("server-nav", "SwapMiddlewarePlugin/Nav");

        base.Execute(services);
    }
}
