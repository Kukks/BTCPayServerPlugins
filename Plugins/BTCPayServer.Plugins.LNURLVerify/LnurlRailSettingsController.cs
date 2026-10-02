#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.LNURLVerify;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[Route("plugins/{storeId}/lnurlverify/rails")]
public class LnurlRailSettingsController : Controller
{
    public const string ActivePage = "LNURLRails";
    private readonly StoreRepository _stores;

    public LnurlRailSettingsController(StoreRepository stores) => _stores = stores;

    [HttpGet("")]
    public async Task<IActionResult> Index([FromRoute] string storeId) =>
        View(await _stores.GetSettingAsync<LnurlRailSettings>(storeId, LnurlRailSettings.Key) ?? new LnurlRailSettings());

    [HttpPost("")]
    public async Task<IActionResult> Index([FromRoute] string storeId, LnurlRailSettings settings, string[]? enabledRails)
    {
        await _stores.UpdateSetting(storeId, LnurlRailSettings.Key, settings);
        var store = HttpContext.GetStoreData();
        if (LnurlRailSettings.ApplyEnabledRails(store, enabledRails ?? Array.Empty<string>()))
            await _stores.UpdateStore(store);
        TempData[WellKnownTempData.SuccessMessage] = "LNURL rail settings saved";
        return RedirectToAction(nameof(Index), new { storeId });
    }
}
