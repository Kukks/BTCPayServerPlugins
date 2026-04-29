using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Fido2;
using BTCPayServer.Plugins.Conference.ViewModels;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Conference;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class ConferenceController : Controller
{
    private readonly AppService _appService;
    private readonly StoreRepository _storeRepository;
    private readonly ConferenceProvisioningService _provisioningService;
    private readonly ConferenceReportService _reportService;
    private readonly ConferenceCsvService _csvService;
    private readonly LinkGenerator _linkGenerator;
    private readonly IOptions<BTCPayServerOptions> _btcPayOptions;
    private readonly IServiceProvider _serviceProvider;

    public ConferenceController(
        AppService appService,
        StoreRepository storeRepository,
        ConferenceProvisioningService provisioningService,
        ConferenceReportService reportService,
        ConferenceCsvService csvService,
        LinkGenerator linkGenerator,
        IOptions<BTCPayServerOptions> btcPayOptions,
        IServiceProvider serviceProvider)
    {
        _appService = appService;
        _storeRepository = storeRepository;
        _provisioningService = provisioningService;
        _reportService = reportService;
        _csvService = csvService;
        _linkGenerator = linkGenerator;
        _btcPayOptions = btcPayOptions;
        _serviceProvider = serviceProvider;
    }

    // ─── Settings / Admin ──────────────────────────────────────────

    [HttpGet("~/plugins/Conference/{appId}/update")]
    public async Task<IActionResult> UpdateSettings(string appId, ReportTimeRange? timeRange)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var vm = await BuildSettingsViewModel(app, settings, timeRange ?? ReportTimeRange.Today);
        return View("UpdateConferenceSettings", vm);
    }

    [HttpPost("~/plugins/Conference/{appId}/update")]
    public async Task<IActionResult> UpdateSettings(string appId, ConferenceSettingsViewModel vm)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        settings.DefaultLightningConnectionString = vm.DefaultLightningConnectionString;
        settings.DefaultCurrency = vm.DefaultCurrency;
        settings.DefaultSpread = vm.DefaultSpread;

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] = "Conference settings updated";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
    }

    // Backwards-compat redirect for old Dashboard bookmarks
    [HttpGet("~/plugins/Conference/{appId}")]
    public IActionResult Dashboard(string appId, ReportTimeRange? timeRange)
    {
        return RedirectToAction(nameof(UpdateSettings), new { appId, timeRange, tab = "merchants" });
    }

    // ─── Merchant CRUD ─────────────────────────────────────────────

    [HttpPost("~/plugins/Conference/{appId}/merchants/add")]
    public async Task<IActionResult> AddMerchant(string appId,
        [FromForm] List<string> emails,
        [FromForm] List<string> storeNames,
        [FromForm] List<string> currencies,
        [FromForm] List<string> spreads,
        [FromForm] List<string> passwords)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var added = 0;
        var skipped = new List<string>();
        var count = emails?.Count ?? 0;

        for (var i = 0; i < count; i++)
        {
            var email = emails[i]?.Trim();
            var storeName = storeNames != null && i < storeNames.Count ? storeNames[i]?.Trim() : null;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(storeName))
                continue;

            if (settings.Merchants.Any(m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            {
                skipped.Add($"{email} (exists)");
                continue;
            }

            var currency = currencies != null && i < currencies.Count ? currencies[i]?.Trim() : null;
            var spreadStr = spreads != null && i < spreads.Count ? spreads[i]?.Trim() : null;
            var password = passwords != null && i < passwords.Count ? passwords[i]?.Trim() : null;

            settings.Merchants.Add(new ConferenceMerchant
            {
                Email = email,
                StoreName = storeName,
                Currency = string.IsNullOrWhiteSpace(currency) ? null : currency,
                Spread = decimal.TryParse(spreadStr, out var sp) ? sp : null,
                Password = string.IsNullOrWhiteSpace(password) ? null : password
            });
            added++;
        }

        if (added == 0 && skipped.Count == 0)
        {
            TempData[WellKnownTempData.ErrorMessage] = "No merchants to add";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        var msg = added == 1 ? $"Merchant {emails[0]?.Trim()} added" : $"Added {added} merchant(s)";
        if (skipped.Count > 0)
            msg += $", skipped {skipped.Count}: {string.Join("; ", skipped)}";
        TempData[added > 0 ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = msg;
        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    [HttpPost("~/plugins/Conference/{appId}/merchants/update")]
    public async Task<IActionResult> UpdateMerchant(string appId, [FromForm] string email, MerchantViewModel updated)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var merchant = settings.Merchants.FirstOrDefault(
            m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (merchant == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Merchant not found";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        merchant.StoreName = updated.StoreName;
        merchant.Currency = string.IsNullOrWhiteSpace(updated.Currency) ? null : updated.Currency;
        merchant.Spread = updated.Spread;
        merchant.LightningConnectionString = string.IsNullOrWhiteSpace(updated.LightningConnectionString)
            ? null
            : updated.LightningConnectionString;

        // Push changes to actual store if provisioned
        if (!string.IsNullOrEmpty(merchant.StoreId))
        {
            await _provisioningService.ReapplySettings(
                new List<ConferenceMerchant> { merchant }, settings,
                forceLightning: true, forceCurrency: true, forceSpread: true);
        }

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] = $"Merchant {email} updated";
        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    [HttpPost("~/plugins/Conference/{appId}/merchants/remove")]
    public async Task<IActionResult> RemoveMerchant(string appId, [FromForm] string email)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var merchant = settings.Merchants.FirstOrDefault(
            m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (merchant == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Merchant not found";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        // Archive store and POS rather than deleting
        await _provisioningService.ArchiveMerchant(merchant);
        settings.Merchants.Remove(merchant);

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] = $"Merchant {email} removed and store archived";
        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    // ─── CSV ───────────────────────────────────────────────────────

    [HttpGet("~/plugins/Conference/{appId}/csv/template")]
    public IActionResult DownloadCsvTemplate(string appId)
    {
        var template = _csvService.GenerateTemplate();
        return File(template, "text/csv", "conference-merchants-template.csv");
    }

    [HttpPost("~/plugins/Conference/{appId}/csv/import")]
    public async Task<IActionResult> ImportCsv(string appId)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var file = Request.Form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
        {
            TempData[WellKnownTempData.ErrorMessage] = "No file uploaded";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        var settings = app.GetSettings<ConferenceSettings>();

        using var stream = file.OpenReadStream();
        var result = _csvService.ParseCsv(stream, settings.Merchants);

        if (result.HasErrors)
        {
            TempData[WellKnownTempData.ErrorMessage] = string.Join("; ", result.Errors);
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] =
            $"CSV imported: {result.Added} added, {result.Updated} updated";
        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    // ─── Provisioning ──────────────────────────────────────────────

    [HttpPost("~/plugins/Conference/{appId}/provision")]
    public async Task<IActionResult> ProvisionAll(string appId)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var adminUserId = GetUserId();
        var succeeded = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var merchant in settings.Merchants.Where(m => !m.IsProvisioned))
        {
            var result = await _provisioningService.ProvisionMerchant(adminUserId, merchant, settings);
            if (result.Success)
                succeeded++;
            else
            {
                failed++;
                errors.Add($"{merchant.Email}: {result.Error}");
            }
        }

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        if (failed > 0)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Provisioned {succeeded}, failed {failed}: {string.Join("; ", errors)}";
        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = $"Provisioned {succeeded} merchants";
        }

        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    [HttpPost("~/plugins/Conference/{appId}/merchants/provision")]
    public async Task<IActionResult> ProvisionMerchant(string appId, [FromForm] string email)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var merchant = settings.Merchants.FirstOrDefault(
            m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (merchant == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Merchant not found";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        if (merchant.IsProvisioned)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"{email} is already provisioned";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        var result = await _provisioningService.ProvisionMerchant(GetUserId(), merchant, settings);

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        if (result.Success)
            TempData[WellKnownTempData.SuccessMessage] = $"Provisioned {email}";
        else
            TempData[WellKnownTempData.ErrorMessage] = $"Failed to provision {email}: {result.Error}";

        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    [HttpPost("~/plugins/Conference/{appId}/merchants/repair")]
    public async Task<IActionResult> RepairMerchant(string appId, [FromForm] string email)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var merchant = settings.Merchants.FirstOrDefault(
            m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (merchant == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Merchant not found";
            return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
        }

        var result = await _provisioningService.RepairMerchant(GetUserId(), merchant, settings);

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        if (result.Success)
        {
            TempData[WellKnownTempData.SuccessMessage] =
                $"Repaired: {string.Join(", ", result.RepairedComponents)}";
        }
        else
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Repair failed: {result.Error}";
        }

        return RedirectToAction(nameof(UpdateSettings), new { appId, tab = "merchants" });
    }

    [HttpPost("~/plugins/Conference/{appId}/reapply")]
    public async Task<IActionResult> ReapplySettings(
        string appId, bool reapplyLightning, bool reapplyCurrency, bool reapplySpread)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();

        await _provisioningService.ReapplySettings(
            settings.Merchants, settings,
            reapplyLightning, reapplyCurrency, reapplySpread);

        TempData[WellKnownTempData.SuccessMessage] = "Settings re-applied to all merchant stores";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
    }

    // ─── Login Code (AJAX) ────────────────────────────────────────

    [HttpPost("~/plugins/Conference/{appId}/merchants/logincode")]
    public async Task<IActionResult> GenerateLoginCode(string appId, [FromForm] string email)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var merchant = settings.Merchants.FirstOrDefault(
            m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

        if (merchant == null)
            return NotFound();

        if (!merchant.UserCreatedByPlugin || string.IsNullOrEmpty(merchant.UserId))
            return BadRequest("Login codes are only available for plugin-created users");

        string posLink = null;
        if (!string.IsNullOrEmpty(merchant.PosAppId))
        {
            posLink = _linkGenerator.GetPathByAction(
                "ViewPointOfSale", "UIPointOfSale",
                new { appId = merchant.PosAppId },
                _btcPayOptions.Value.RootPath);
        }

        if (string.IsNullOrEmpty(posLink))
            return BadRequest("Merchant has no POS configured");

        var loginCodeUrl = GenerateLoginCodeUrl(merchant.UserId, posLink);
        return PartialView("_LoginCodePartial", loginCodeUrl);
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private async Task<AppData> GetConferenceApp(string appId)
    {
        return await _appService.GetAppDataIfOwner(GetUserId(), appId, ConferenceApp.AppType);
    }

    private string GetUserId() => User.GetId();

    private string GenerateLoginCodeUrl(string userId, string redirectUrl)
    {
        using var scope = _serviceProvider.CreateScope();
        var loginCodeService = scope.ServiceProvider.GetRequiredService<UserLoginCodeService>();
        var loginCode = loginCodeService.GetOrGenerate(userId);

        var request = HttpContext.Request;
        return _linkGenerator.LoginCodeLink(
            loginCode, redirectUrl,
            request.Scheme, request.Host, request.PathBase);
    }

    private async Task<ConferenceSettingsViewModel> BuildSettingsViewModel(
        AppData app, ConferenceSettings settings, ReportTimeRange timeRange)
    {
        var vm = new ConferenceSettingsViewModel
        {
            AppId = app.Id,
            AppName = app.Name,
            DefaultLightningConnectionString = settings.DefaultLightningConnectionString,
            DefaultCurrency = settings.DefaultCurrency,
            DefaultSpread = settings.DefaultSpread,
            SelectedTimeRange = timeRange
        };

        // Generate sales report for provisioned merchants
        var provisionedMerchants = settings.Merchants.Where(m => m.IsProvisioned).ToList();
        if (provisionedMerchants.Count > 0)
        {
            vm.Report = await _reportService.GenerateReport(
                settings.Merchants, timeRange, HttpContext.RequestAborted);
        }

        foreach (var merchant in settings.Merchants)
        {
            var health = await _provisioningService.CheckMerchantHealth(merchant);
            var mvm = new MerchantViewModel
            {
                Email = merchant.Email,
                StoreName = merchant.StoreName,
                Currency = merchant.Currency,
                Spread = merchant.Spread,
                LightningConnectionString = merchant.LightningConnectionString,
                UserId = merchant.UserId,
                StoreId = merchant.StoreId,
                PosAppId = merchant.PosAppId,
                Status = health.Summary,
                IsProvisioned = merchant.IsProvisioned,
                HasError = !health.IsHealthy && !health.IsNotProvisioned,
                IsExistingUser = !merchant.UserCreatedByPlugin && !string.IsNullOrEmpty(merchant.UserId),
                CanGenerateLoginCode = merchant.UserCreatedByPlugin && !string.IsNullOrEmpty(merchant.UserId)
            };

            if (!string.IsNullOrEmpty(merchant.PosAppId))
            {
                mvm.PosLink = _linkGenerator.GetPathByAction(
                    "ViewPointOfSale", "UIPointOfSale",
                    new { appId = merchant.PosAppId },
                    _btcPayOptions.Value.RootPath);
            }

            mvm.CanGenerateLoginCode = mvm.CanGenerateLoginCode && !string.IsNullOrEmpty(mvm.PosLink);

            // Attach merchant report if available
            if (vm.Report != null)
            {
                mvm.MerchantReport = vm.Report.MerchantReports.FirstOrDefault(
                    r => r.StoreId == merchant.StoreId);
            }

            vm.Merchants.Add(mvm);
        }

        return vm;
    }
}
