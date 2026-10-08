#nullable enable
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LnurlTokenServerSettings
{
    private static readonly Regex ProjectId = new(@"\A[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant);

    public string? WalletConnectProjectId { get; set; }

    public static bool IsValidProjectId(string? id) => id is null || ProjectId.IsMatch(id);
}

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
[Route("server/lnurlverify")]
public class LnurlVerifyServerController : Controller
{
    public const string ActivePage = "LNURLVerifyServer";
    private readonly ISettingsRepository _settings;

    public LnurlVerifyServerController(ISettingsRepository settings) => _settings = settings;

    [HttpGet("")]
    public async Task<IActionResult> Index() =>
        View(await _settings.GetSettingAsync<LnurlTokenServerSettings>() ?? new LnurlTokenServerSettings());

    [HttpPost("")]
    public async Task<IActionResult> Index(LnurlTokenServerSettings settings)
    {
        settings.WalletConnectProjectId = string.IsNullOrWhiteSpace(settings.WalletConnectProjectId) ? null : settings.WalletConnectProjectId.Trim();
        if (!LnurlTokenServerSettings.IsValidProjectId(settings.WalletConnectProjectId))
        {
            ModelState.AddModelError(nameof(settings.WalletConnectProjectId), "A WalletConnect project ID is 32 hexadecimal characters.");
            return View(settings);
        }
        await _settings.UpdateSetting(settings);
        TempData[WellKnownTempData.SuccessMessage] = "LNURL Verify settings saved";
        return RedirectToAction(nameof(Index));
    }
}
