#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[Route("plugins/{storeId}/lnurlverify/rails")]
public class LnurlRailSettingsController : Controller
{
    public const string ActivePage = "LNURLRails";
    private readonly StoreRepository _stores;
    private readonly TokenAssets _assets;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Network _network;

    public LnurlRailSettingsController(StoreRepository stores, TokenAssets assets, IHttpClientFactory httpClientFactory,
        BTCPayNetworkProvider networks)
    {
        _stores = stores;
        _assets = assets;
        _httpClientFactory = httpClientFactory;
        _network = networks.BTC.NBitcoinNetwork;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromRoute] string storeId)
    {
        ViewData["UnconfiguredUnits"] = await Unconfigured(HttpContext.GetStoreData());
        return View(await _stores.GetSettingAsync<LnurlRailSettings>(storeId, LnurlRailSettings.Key) ?? new LnurlRailSettings());
    }

    [HttpPost("")]
    public async Task<IActionResult> Index([FromRoute] string storeId, LnurlRailSettings settings,
        string[]? shownRails, string[]? enabledRails)
    {
        await _stores.UpdateSetting(storeId, LnurlRailSettings.Key, settings);
        var store = HttpContext.GetStoreData();
        var managed = LnurlRails.All.Select(r => r.PaymentMethodId).Concat(_assets.PaymentMethodIds);
        if (LnurlRailSettings.ApplyEnabledRails(store, managed, shownRails ?? Array.Empty<string>(), enabledRails ?? Array.Empty<string>()))
            await _stores.UpdateStoreBlob(store);
        TempData[WellKnownTempData.SuccessMessage] = "LNURL rail settings saved";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    /// <summary>The Lightning setup page's preview of an LNURL before it is saved.</summary>
    [HttpPost("resolve")]
    public async Task<IActionResult> Resolve([FromRoute] string storeId, string? lnurl)
    {
        if (string.IsNullOrWhiteSpace(lnurl)) return BadRequest(new { error = "Enter a Lightning address or LNURL." });
        var http = _httpClientFactory.CreateClient(nameof(LnurlRailSettingsController));
        http.Timeout = TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return Json(await LnurlSetupSummary.Read(lnurl.Trim(), HttpContext.GetStoreData(), _assets, _network, http, cts.Token));
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return BadRequest(new { error = "The LNURL did not answer within 10 seconds." });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return BadRequest(new { error = e.Message });
        }
    }

    private async Task<IReadOnlyList<string>> Unconfigured(StoreData store)
    {
        if (LnurlRailProvisioning.LnurlValue(store) is not { } lnurl) return Array.Empty<string>();
        try
        {
            var http = _httpClientFactory.CreateClient(nameof(LnurlRailSettingsController));
            http.Timeout = TimeSpan.FromSeconds(10);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var resolved = await LNURLVerifyConnectionStringHandler.ResolveCached(lnurl, _network, http, cts.Token);
            return _assets.Unconfigured(await LNURLResolver.GetJson(http, resolved.PayEndpoint, cts.Token));
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
