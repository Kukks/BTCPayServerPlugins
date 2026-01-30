#nullable enable
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SwapMiddleware.Services;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.SwapMiddleware.Controllers;

/// <summary>
/// Public API controller for swap middleware proxy endpoints.
/// These endpoints are called by other plugins and do not require authentication.
/// </summary>
[Route("plugins/swap-middleware/api")]
[ApiController]
public class SwapMiddlewareApiController : ControllerBase
{
    private readonly SwapMiddlewareService _service;

    public SwapMiddlewareApiController(SwapMiddlewareService service)
    {
        _service = service;
    }

    private string? GetUserIp()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    // === SideShift Proxy Endpoints ===

    /// <summary>
    /// Proxy for SideShift coins endpoint (cached)
    /// GET /plugins/swap-middleware/api/sideshift/coins
    /// </summary>
    [HttpGet("sideshift/coins")]
    public async Task<IActionResult> GetSideShiftCoins()
    {
        var result = await _service.GetSideShiftCoins(GetUserIp());
        return new ContentResult
        {
            Content = result.Content,
            ContentType = "application/json",
            StatusCode = result.StatusCode
        };
    }

    /// <summary>
    /// Proxy for SideShift facts/deposit methods endpoint (cached)
    /// GET /plugins/swap-middleware/api/sideshift/facts
    /// </summary>
    [HttpGet("sideshift/facts")]
    public async Task<IActionResult> GetSideShiftFacts()
    {
        var result = await _service.GetSideShiftFacts(GetUserIp());
        return new ContentResult
        {
            Content = result.Content,
            ContentType = "application/json",
            StatusCode = result.StatusCode
        };
    }

    /// <summary>
    /// Proxy for SideShift variable shift creation - injects affiliateId
    /// POST /plugins/swap-middleware/api/sideshift/shifts/variable
    /// </summary>
    [HttpPost("sideshift/shifts/variable")]
    public async Task<IActionResult> CreateSideShiftVariableShift()
    {
        using var reader = new StreamReader(Request.Body);
        var requestBody = await reader.ReadToEndAsync();

        var result = await _service.CreateSideShiftVariableShift(requestBody, GetUserIp());

        return new ContentResult
        {
            Content = result.Content,
            ContentType = "application/json",
            StatusCode = result.StatusCode
        };
    }

    // === FixedFloat Endpoints ===

    /// <summary>
    /// Get FixedFloat configuration (ref code for widget)
    /// GET /plugins/swap-middleware/api/fixedfloat/config
    /// </summary>
    [HttpGet("fixedfloat/config")]
    public async Task<IActionResult> GetFixedFloatConfig()
    {
        var config = await _service.GetFixedFloatConfig();
        return Ok(config);
    }
}
