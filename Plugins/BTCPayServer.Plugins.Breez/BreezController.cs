using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Breez;
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/Breez")]
public class BreezController : Controller
{
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly BreezService _breezService;

    public BreezController(BTCPayNetworkProvider btcPayNetworkProvider, BreezService breezService)
    {
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _breezService = breezService;
    }
[HttpGet("")]
    public async Task<IActionResult> Index(string storeId)
    {
        return View(await _breezService.Get(storeId));
        
    }
    [HttpPost("")]
    public async Task<IActionResult> Index(string storeId, string command, BreezSettings settings)
    {
        if (command == "clear")
        {
            await _breezService.Set(storeId, null);
            TempData[WellKnownTempData.SuccessMessage] = "Settings cleared successfully";
            return RedirectToAction(nameof(Index), new {storeId});
        }

        if (command == "save")
        {
            try
            {
                await _breezService.Set(storeId, settings);
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Couldnt use provided settings: {e.Message}";
                return View(settings);
            }
            
            TempData[WellKnownTempData.SuccessMessage] = "Settings saved successfully";
            return RedirectToAction(nameof(Index), new {storeId});
        }

        return NotFound();
    }
}