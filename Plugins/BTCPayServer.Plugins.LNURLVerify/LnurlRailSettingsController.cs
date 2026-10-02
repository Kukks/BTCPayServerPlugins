#nullable enable
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
    public async Task<IActionResult> Index(string storeId) =>
        View(await _stores.GetSettingAsync<LnurlRailSettings>(storeId, LnurlRailSettings.Key) ?? new LnurlRailSettings());

    [HttpPost("")]
    public async Task<IActionResult> Index(string storeId, LnurlRailSettings settings)
    {
        await _stores.UpdateSetting(storeId, LnurlRailSettings.Key, settings);
        TempData[WellKnownTempData.SuccessMessage] = "LNURL rail settings saved";
        return RedirectToAction(nameof(Index), new { storeId });
    }
}
