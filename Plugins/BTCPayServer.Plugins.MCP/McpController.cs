using BTCPayServer.Abstractions.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.MCP;

/// <summary>
/// Registers the MCP Streamable HTTP endpoint at /plugins/mcp.
/// The ModelContextProtocol.AspNetCore SDK uses MapMcp() endpoint routing
/// rather than MVC controllers, so this class provides endpoint registration
/// instead of inheriting from ControllerBase.
/// </summary>
public static class McpEndpointRegistration
{
    public static void MapMcpEndpoint(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp("plugins/mcp")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = AuthenticationSchemes.Greenfield
            });
    }
}
