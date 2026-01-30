#nullable enable
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SwapMiddleware.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.SwapMiddleware.Controllers;

/// <summary>
/// Admin controller for configuring SwapMiddleware settings (server-level).
/// </summary>
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("~/plugins/swap-middleware")]
public class SwapMiddlewareController : Controller
{
    private readonly SwapMiddlewareService _service;

    public SwapMiddlewareController(SwapMiddlewareService service)
    {
        _service = service;
    }

    [HttpGet("")]
    public async Task<IActionResult> Configure()
    {
        var settings = await _service.GetSettings();
        return View(settings);
    }

    [HttpPost("")]
    public async Task<IActionResult> Configure(SwapMiddlewareSettings settings, string command)
    {
        switch (command?.ToLowerInvariant())
        {
            case "save":
                // Validate SideShift affiliate ID format (alphanumeric)
                if (!string.IsNullOrEmpty(settings.SideShiftAffiliateId) &&
                    !Regex.IsMatch(settings.SideShiftAffiliateId, @"^[a-zA-Z0-9]+$"))
                {
                    ModelState.AddModelError(
                        nameof(settings.SideShiftAffiliateId),
                        "Affiliate ID must be alphanumeric");
                }

                // Validate FixedFloat ref code format
                if (!string.IsNullOrEmpty(settings.FixedFloatRefCode) &&
                    !Regex.IsMatch(settings.FixedFloatRefCode, @"^[a-zA-Z0-9]+$"))
                {
                    ModelState.AddModelError(
                        nameof(settings.FixedFloatRefCode),
                        "Referral code must be alphanumeric");
                }

                // Validate cache duration
                if (settings.CacheDurationMinutes < 1 || settings.CacheDurationMinutes > 60)
                {
                    ModelState.AddModelError(
                        nameof(settings.CacheDurationMinutes),
                        "Cache duration must be between 1 and 60 minutes");
                }

                if (!ModelState.IsValid)
                    return View(settings);

                await _service.UpdateSettings(settings);

                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Severity = StatusMessageModel.StatusSeverity.Success,
                    Message = "SwapMiddleware settings saved"
                });

                return RedirectToAction(nameof(Configure));

            default:
                return View(settings);
        }
    }
}
