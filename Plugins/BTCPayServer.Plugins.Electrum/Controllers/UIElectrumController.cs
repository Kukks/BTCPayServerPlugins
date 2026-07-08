using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Controllers;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins;
using BTCPayServer.Plugins.Electrum.Data;
using BTCPayServer.Plugins.Electrum.Models;
using BTCPayServer.Plugins.Electrum.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.Electrum.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
public class UIElectrumController : Controller
{
    private readonly SettingsRepository _settingsRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly IConfiguration _configuration;

    public UIElectrumController(
        SettingsRepository settingsRepository,
        BTCPayNetworkProvider networkProvider,
        IOptions<DataDirectories> dataDirectories,
        IConfiguration configuration)
    {
        _settingsRepository = settingsRepository;
        _networkProvider = networkProvider;
        _dataDirectories = dataDirectories;
        _configuration = configuration;
    }

    // These controller routes are registered as MVC application parts independently
    // of ElectrumPlugin.Execute, so they still resolve when Electrum is inactive
    // (non-mainnet, where Execute returns early and registers no Electrum services).
    // Short-circuit here so an admin hitting the URL sees a clear "inactive" response
    // instead of a 500 from unresolved services.
    //
    // Must mirror ElectrumPlugin.Execute's own mainnet gate exactly, including its
    // AllowNonMainnet(IConfiguration) escape hatch: otherwise, with the escape hatch
    // set (e.g. BTCPAY_ELECTRUM_ALLOWNONMAINNET=true for regtest testing),
    // ElectrumPlugin.Execute registers BackendCoordinator/ElectrumDbContextFactory/
    // ElectrumStatusMonitor (this controller's action can resolve and use them), yet
    // this filter would still 404 every request because it never re-checked the
    // escape hatch — found by the P4 Task 4 integration batch.
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (_networkProvider.NetworkType != ChainName.Mainnet && !ElectrumPlugin.AllowNonMainnet(_configuration))
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
        ViewBag.Coexistence = await BuildCoexistenceStatusAsync();
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
            ViewBag.Coexistence = await BuildCoexistenceStatusAsync();
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

    // Read-only coexistence status panel (P4 Task 3): per-wallet active backend + hysteresis
    // agreement count from BackendCoordinator, reserved indexes from the ledger's TrackedWallets
    // rows, and the current effective Electrum/NBX status from ElectrumStatusMonitor.
    //
    // BackendCoordinator/ElectrumDbContextFactory/ElectrumStatusMonitor are resolved lazily from
    // HttpContext.RequestServices rather than injected via the constructor: they are only
    // registered when ElectrumPlugin.Execute runs (mainnet), and this controller must stay
    // constructible on non-mainnet so OnActionExecuting's short-circuit above can return a clean
    // NotFound instead of a DI resolution failure.
    private async Task<CoexistenceStatusViewModel> BuildCoexistenceStatusAsync()
    {
        var coordinator = HttpContext.RequestServices.GetRequiredService<BackendCoordinator>();
        var dbFactory = HttpContext.RequestServices.GetRequiredService<ElectrumDbContextFactory>();
        var statusMonitor = HttpContext.RequestServices.GetRequiredService<ElectrumStatusMonitor>();

        List<TrackedWallet> wallets;
        await using (var ctx = dbFactory.CreateContext())
        {
            wallets = await ctx.TrackedWallets.AsNoTracking().ToListAsync();
        }

        var states = coordinator.SnapshotStates().ToDictionary(s => s.WalletId);

        return new CoexistenceStatusViewModel
        {
            EffectiveReady = statusMonitor.State == NBXplorerState.Ready,
            ElectrumConnectedServer = statusMonitor.ConnectedServer,
            ElectrumConfiguredServer = statusMonitor.ConfiguredServer,
            ElectrumServerVersion = statusMonitor.ServerVersion,
            ElectrumTipHeight = statusMonitor.TipHeight,
            Wallets = wallets.Select(w =>
            {
                states.TryGetValue(w.Id, out var state);
                return new WalletCoexistenceRow
                {
                    WalletId = w.Id,
                    Active = state?.Active ?? WalletBackend.Electrum,
                    ConsecutiveAgree = state?.ConsecutiveAgree ?? 0,
                    ReservedReceiveIndex = w.ReservedReceiveIndex,
                    ReservedChangeIndex = w.ReservedChangeIndex
                };
            }).ToList()
        };
    }
}
