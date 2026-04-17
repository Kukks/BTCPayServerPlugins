using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using Microsoft.AspNetCore.Identity;
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
    public async Task<IActionResult> UpdateSettings(string appId)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var vm = await BuildSettingsViewModel(app, settings);
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

    // ─── Merchant CRUD ─────────────────────────────────────────────

    [HttpPost("~/plugins/Conference/{appId}/merchants/add")]
    public async Task<IActionResult> AddMerchant(string appId, MerchantViewModel merchant)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();

        if (settings.Merchants.Any(m => m.Email.Equals(merchant.Email, StringComparison.OrdinalIgnoreCase)))
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Merchant with email {merchant.Email} already exists";
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        settings.Merchants.Add(new ConferenceMerchant
        {
            Email = merchant.Email,
            StoreName = merchant.StoreName,
            Currency = string.IsNullOrWhiteSpace(merchant.Currency) ? null : merchant.Currency,
            Spread = merchant.Spread,
            LightningConnectionString = string.IsNullOrWhiteSpace(merchant.LightningConnectionString)
                ? null
                : merchant.LightningConnectionString,
            Password = merchant.Password
        });

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] = $"Merchant {merchant.Email} added";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
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
            return RedirectToAction(nameof(UpdateSettings), new { appId });
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
        return RedirectToAction(nameof(UpdateSettings), new { appId });
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
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        // Archive store and POS rather than deleting
        await _provisioningService.ArchiveMerchant(merchant);
        settings.Merchants.Remove(merchant);

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] = $"Merchant {email} removed and store archived";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
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
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        var settings = app.GetSettings<ConferenceSettings>();

        using var stream = file.OpenReadStream();
        var result = _csvService.ParseCsv(stream, settings.Merchants);

        if (result.HasErrors)
        {
            TempData[WellKnownTempData.ErrorMessage] = string.Join("; ", result.Errors);
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        TempData[WellKnownTempData.SuccessMessage] =
            $"CSV imported: {result.Added} added, {result.Updated} updated";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
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

        return RedirectToAction(nameof(UpdateSettings), new { appId });
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
            return RedirectToAction(nameof(UpdateSettings), new { appId });
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

        return RedirectToAction(nameof(UpdateSettings), new { appId });
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

    // ─── Dashboard ─────────────────────────────────────────────────

    [HttpGet("~/plugins/Conference/{appId}")]
    public async Task<IActionResult> Dashboard(string appId, ReportTimeRange? timeRange)
    {
        var app = await GetConferenceApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<ConferenceSettings>();
        var selectedRange = timeRange ?? ReportTimeRange.Today;

        // Generate report
        var report = await _reportService.GenerateReport(
            settings.Merchants, selectedRange, HttpContext.RequestAborted);

        // Build dashboard view model with live store data
        var vm = new ConferenceDashboardViewModel
        {
            AppId = appId,
            AppName = app.Name,
            StoreId = app.StoreDataId,
            SelectedTimeRange = selectedRange,
            Report = report
        };

        foreach (var merchant in settings.Merchants)
        {
            var dashMerchant = new DashboardMerchantViewModel
            {
                Email = merchant.Email,
                StoreName = merchant.StoreName,
                StoreId = merchant.StoreId,
                PosAppId = merchant.PosAppId,
                IsExistingUser = !merchant.UserCreatedByPlugin && !string.IsNullOrEmpty(merchant.UserId)
            };

            // Fetch live data from store
            if (!string.IsNullOrEmpty(merchant.StoreId))
            {
                var store = await _storeRepository.FindStore(merchant.StoreId);
                if (store != null)
                {
                    var blob = store.GetStoreBlob();
                    dashMerchant.StoreCurrency = blob.DefaultCurrency;
                    dashMerchant.Spread = blob.Spread * 100m;
                    dashMerchant.StoreLink = _linkGenerator.GetPathByAction(
                        "Dashboard", "UIStores", new { storeId = merchant.StoreId },
                        _btcPayOptions.Value.RootPath);
                    dashMerchant.Status = "Ready";
                }
                else
                {
                    dashMerchant.Status = "Store deleted";
                    dashMerchant.HasError = true;
                }
            }
            else
            {
                dashMerchant.Status = "Not provisioned";
                dashMerchant.HasError = true;
            }

            // POS link
            if (!string.IsNullOrEmpty(merchant.PosAppId))
            {
                var posApp = await _appService.GetApp(merchant.PosAppId, null, includeArchived: true);
                if (posApp != null)
                {
                    dashMerchant.PosLink = _linkGenerator.GetPathByAction(
                        "ViewPointOfSale", "UIPointOfSale",
                        new { appId = merchant.PosAppId },
                        _btcPayOptions.Value.RootPath);
                }
                else
                {
                    dashMerchant.Status = "POS deleted";
                    dashMerchant.HasError = true;
                }
            }

            // SECURITY: Only generate login codes for users the plugin created.
            // Pre-existing users (e.g., server admins) must NOT get login codes
            // generated for them — that would be a privilege escalation exploit.
            if (merchant.UserCreatedByPlugin &&
                !string.IsNullOrEmpty(merchant.UserId) &&
                !string.IsNullOrEmpty(dashMerchant.PosLink))
            {
                dashMerchant.LoginCodeUrl = GenerateLoginCodeUrl(merchant.UserId, dashMerchant.PosLink);
            }

            // Attach merchant report if available
            dashMerchant.MerchantReport = report.MerchantReports.FirstOrDefault(
                r => r.StoreId == merchant.StoreId);

            vm.Merchants.Add(dashMerchant);
        }

        return View("Dashboard", vm);
    }

    // ─── Helpers ───────────────────────────────────────────────────

    private async Task<AppData> GetConferenceApp(string appId)
    {
        return await _appService.GetApp(appId, ConferenceApp.AppType);
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
        AppData app, ConferenceSettings settings)
    {
        var vm = new ConferenceSettingsViewModel
        {
            AppId = app.Id,
            AppName = app.Name,
            DefaultLightningConnectionString = settings.DefaultLightningConnectionString,
            DefaultCurrency = settings.DefaultCurrency,
            DefaultSpread = settings.DefaultSpread
        };

        foreach (var merchant in settings.Merchants)
        {
            var health = await _provisioningService.CheckMerchantHealth(merchant);
            vm.Merchants.Add(new MerchantViewModel
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
                HasError = !health.IsHealthy && !health.IsNotProvisioned
            });
        }

        return vm;
    }
}
