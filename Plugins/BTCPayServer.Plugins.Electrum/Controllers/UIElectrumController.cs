using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.Electrum.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
public class UIElectrumController : Controller
{
    private readonly SettingsRepository _settingsRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IOptions<DataDirectories> _dataDirectories;

    public UIElectrumController(
        SettingsRepository settingsRepository,
        BTCPayNetworkProvider networkProvider,
        IOptions<DataDirectories> dataDirectories)
    {
        _settingsRepository = settingsRepository;
        _networkProvider = networkProvider;
        _dataDirectories = dataDirectories;
    }

    // These controller routes are registered as MVC application parts independently
    // of ElectrumPlugin.Execute, so they still resolve when Electrum is inactive
    // (non-mainnet, where Execute returns early and registers no Electrum services).
    // Short-circuit here so an admin hitting the URL sees a clear "inactive" response
    // instead of a 500 from unresolved services.
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (_networkProvider.NetworkType != ChainName.Mainnet)
        {
            context.Result = NotFound(
                $"Electrum is only active on Bitcoin mainnet; it is inactive on {_networkProvider.NetworkType}.");
            return;
        }
        base.OnActionExecuting(context);
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
                    settings,
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

    [HttpPost("~/server/electrum/disable")]
    public IActionResult Disable()
    {
        PluginManager.QueueCommands(_dataDirectories.Value.PluginDir,
            ("disable", "BTCPayServer.Plugins.Electrum"));
        TempData[WellKnownTempData.SuccessMessage] =
            "Electrum plugin will be disabled on next restart. NBXplorer will be re-enabled.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("~/server/electrum/sync-status")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult SyncStatus()
    {
        return PartialView("Electrum/_SyncStatusContent");
    }
}
