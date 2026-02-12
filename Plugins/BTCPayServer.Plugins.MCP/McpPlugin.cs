using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.AspNetCore.Builder;
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
        base.Execute(services);
    }

    public override void Execute(IApplicationBuilder applicationBuilder,
        IServiceProvider applicationBuilderApplicationServices)
    {
        // Middleware to promote ?token= query parameter to Authorization header.
        // This enables Claude Connectors (web/mobile) which can only pass a URL, not custom headers.
        applicationBuilder.UseWhen(
            context => context.Request.Path.StartsWithSegments("/plugins/mcp"),
            app => app.Use(async (context, next) =>
            {
                if (!context.Request.Headers.ContainsKey("Authorization") &&
                    context.Request.Query.TryGetValue("token", out var token) &&
                    !string.IsNullOrEmpty(token))
                {
                    context.Request.Headers["Authorization"] = $"token {token}";
                }
                await next();
            }));

        applicationBuilder.UseEndpoints(endpoints =>
        {
            McpEndpointRegistration.MapMcpEndpoint(endpoints);
        });
        base.Execute(applicationBuilder, applicationBuilderApplicationServices);
    }
}
