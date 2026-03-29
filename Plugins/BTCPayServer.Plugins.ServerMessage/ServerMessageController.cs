using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.ServerMessage;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("~/plugins/server-message")]
public class ServerMessageController : Controller
{
    private readonly ISettingsRepository _settingsRepository;

    public ServerMessageController(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    [HttpGet("")]
    public async Task<IActionResult> Update()
    {
        var settings = await _settingsRepository.GetSettingAsync<ServerMessageSettings>()
                       ?? new ServerMessageSettings();
        return View(settings);
    }

    [HttpPost("")]
    public async Task<IActionResult> Update(ServerMessageSettings settings)
    {
        if (!ModelState.IsValid)
            return View(settings);

        await _settingsRepository.UpdateSetting(settings);
        TempData["SuccessMessage"] = "Server message settings saved";
        return RedirectToAction(nameof(Update));
    }
}
