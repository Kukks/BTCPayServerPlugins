using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.MCP;

public class McpPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("header-nav", "MCP/McpPluginHeaderNav");
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly();
        services.AddScoped<McpHttpClient>();
        services.AddHttpClient("McpGreenfield")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });
        // Insert token middleware before authentication in the pipeline
        services.AddTransient<IStartupFilter, McpTokenStartupFilter>();
        base.Execute(services);
    }

    public override void Execute(IApplicationBuilder applicationBuilder,
        IServiceProvider applicationBuilderApplicationServices)
    {
        applicationBuilder.UseEndpoints(endpoints =>
        {
            McpEndpointRegistration.MapMcpEndpoint(endpoints);
        });
        base.Execute(applicationBuilder, applicationBuilderApplicationServices);
    }
}

/// <summary>
/// Inserts middleware early in the pipeline (before authentication) to promote
/// ?token= query parameter to Authorization header for MCP requests.
/// This enables Claude Connectors (web/mobile) which can only pass a URL, not custom headers.
/// </summary>
public class McpTokenStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/plugins/mcp"),
                branch => branch.Use(async (context, next2) =>
                {
                    if (!context.Request.Headers.ContainsKey("Authorization") &&
                        context.Request.Query.TryGetValue("token", out var token) &&
                        !string.IsNullOrEmpty(token))
                    {
                        context.Request.Headers["Authorization"] = $"token {token}";
                    }
                    await next2();
                }));
            next(app);
        };
    }
}
