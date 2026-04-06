using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Controllers;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Electrum.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
public class UIElectrumController : Controller
{
    private readonly SettingsRepository _settingsRepository;
    private readonly ElectrumClient _electrumClient;

    public UIElectrumController(
        SettingsRepository settingsRepository,
        ElectrumClient electrumClient)
    {
        _settingsRepository = settingsRepository;
        _electrumClient = electrumClient;
    }

    [HttpGet("~/server/electrum")]
    public async Task<IActionResult> Settings()
    {
        var settings = await _settingsRepository.GetSettingAsync<ElectrumSettings>() ?? new ElectrumSettings();
        return View(settings);
    }

    [HttpPost("~/server/electrum")]
    public async Task<IActionResult> Settings(ElectrumSettings settings, string command)
    {
        if (command == "test")
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var testClient = new ElectrumClient(
                    Microsoft.Extensions.Options.Options.Create(settings),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ElectrumClient>.Instance);
                await testClient.ConnectAsync(cts.Token);
                var (sw, pv) = await testClient.ServerVersionAsync("BTCPayServer-Electrum", "1.4", cts.Token);
                await testClient.DisposeAsync();
                ViewBag.StatusMessage = $"Connection successful! Server: {sw}, Protocol: {pv}";
            }
            catch (Exception ex)
            {
                ViewBag.StatusMessage = $"Connection failed: {ex.Message}";
            }
            return View(settings);
        }

        await _settingsRepository.UpdateSetting(settings);
        TempData[WellKnownTempData.SuccessMessage] = "Electrum settings updated.";
        return RedirectToAction(nameof(Settings));
    }
}
