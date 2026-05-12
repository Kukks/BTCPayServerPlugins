using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Terminal.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Terminal;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITerminalController : Controller
{
    private readonly AppService _appService;
    private readonly TerminalService _terminalService;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly LinkGenerator _linkGenerator;
    private readonly IOptions<BTCPayServerOptions> _btcPayOptions;

    public UITerminalController(
        AppService appService,
        TerminalService terminalService,
        InvoiceRepository invoiceRepository,
        LinkGenerator linkGenerator,
        IOptions<BTCPayServerOptions> btcPayOptions)
    {
        _appService = appService;
        _terminalService = terminalService;
        _invoiceRepository = invoiceRepository;
        _linkGenerator = linkGenerator;
        _btcPayOptions = btcPayOptions;
    }

    [HttpGet("~/plugins/Terminal/{appId}")]
    public async Task<IActionResult> UpdateSettings(string appId)
    {
        var app = await GetTerminalApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<TerminalSettings>();
        var states = _terminalService.GetTerminalsForApp(appId).ToList();
        var activeTerminalId = HttpContext.Request.Cookies["btcpay-terminal"];

        var vm = new TerminalSettingsViewModel
        {
            AppId = app.Id,
            AppName = app.Name,
            Terminals = settings.Terminals.Select(t =>
            {
                var state = states.FirstOrDefault(s => s.TerminalId == t.Id);
                return new TerminalViewModel
                {
                    Id = t.Id,
                    Name = t.Name,
                    CurrentInvoiceId = state?.CurrentInvoiceId,
                    IsActive = t.Id == activeTerminalId,
                    PublicUrl = GetAbsoluteUrl(nameof(RedirectToInvoice), t.Id),
                    CheckInUrl = GetAbsoluteUrl(nameof(CheckIn), t.Id)
                };
            }).ToList()
        };

        return View(vm);
    }

    [HttpPost("~/plugins/Terminal/{appId}/add")]
    public async Task<IActionResult> AddTerminal(string appId, [FromForm] string name)
    {
        var app = await GetTerminalApp(appId);
        if (app == null) return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Terminal name is required";
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        var settings = app.GetSettings<TerminalSettings>();
        var terminal = new TerminalData { Name = name.Trim() };
        settings.Terminals.Add(terminal);

        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        _terminalService.RegisterTerminals(appId, app.StoreDataId, settings.Terminals);

        TempData[WellKnownTempData.SuccessMessage] = $"Terminal \"{terminal.Name}\" added";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
    }

    [HttpPost("~/plugins/Terminal/{appId}/remove")]
    public async Task<IActionResult> RemoveTerminal(string appId, [FromForm] string terminalId)
    {
        var app = await GetTerminalApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<TerminalSettings>();
        var terminal = settings.Terminals.FirstOrDefault(t => t.Id == terminalId);
        if (terminal == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Terminal not found";
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        settings.Terminals.Remove(terminal);
        app.SetSettings(settings);
        await _appService.UpdateOrCreateApp(app);

        _terminalService.RegisterTerminals(appId, app.StoreDataId, settings.Terminals);

        // Clear cookie if this was the active terminal
        if (HttpContext.Request.Cookies["btcpay-terminal"] == terminalId)
            HttpContext.Response.Cookies.Delete("btcpay-terminal");

        TempData[WellKnownTempData.SuccessMessage] = $"Terminal \"{terminal.Name}\" removed";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
    }

    [HttpPost("~/plugins/Terminal/{appId}/activate")]
    public async Task<IActionResult> ActivateTerminal(string appId, [FromForm] string terminalId)
    {
        var app = await GetTerminalApp(appId);
        if (app == null) return NotFound();

        var settings = app.GetSettings<TerminalSettings>();
        var terminal = settings.Terminals.FirstOrDefault(t => t.Id == terminalId);
        if (terminal == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Terminal not found";
            return RedirectToAction(nameof(UpdateSettings), new { appId });
        }

        HttpContext.Response.Cookies.Append("btcpay-terminal", terminalId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });

        TempData[WellKnownTempData.SuccessMessage] = $"Terminal \"{terminal.Name}\" activated on this browser";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
    }

    [HttpPost("~/plugins/Terminal/{appId}/deactivate")]
    public async Task<IActionResult> DeactivateTerminal(string appId)
    {
        var app = await GetTerminalApp(appId);
        if (app == null) return NotFound();

        HttpContext.Response.Cookies.Delete("btcpay-terminal");

        TempData[WellKnownTempData.SuccessMessage] = "Terminal deactivated on this browser";
        return RedirectToAction(nameof(UpdateSettings), new { appId });
    }

    // Public endpoint — no auth required. NFC/QR tags point here.
    [AllowAnonymous]
    [HttpGet("~/t/{terminalId}")]
    public IActionResult RedirectToInvoice(string terminalId)
    {
        var terminal = _terminalService.GetTerminal(terminalId);
        if (terminal == null)
            return NotFound("Terminal not found");

        if (string.IsNullOrEmpty(terminal.CurrentInvoiceId))
            return View("WaitingForPayment", new WaitingViewModel
            {
                TerminalName = terminal.Name,
                TerminalId = terminal.TerminalId
            });

        var checkoutUrl = _linkGenerator.GetPathByAction(
            "Checkout", "UIInvoice",
            new { invoiceId = terminal.CurrentInvoiceId },
            _btcPayOptions.Value.RootPath);

        return Redirect(checkoutUrl!);
    }

    // Cashier check-in — no auth required. Tap NFC card to bind this browser to a terminal.
    [AllowAnonymous]
    [HttpGet("~/t/{terminalId}/checkin")]
    public IActionResult CheckIn(string terminalId)
    {
        var terminal = _terminalService.GetTerminal(terminalId);
        if (terminal == null)
            return NotFound("Terminal not found");

        HttpContext.Response.Cookies.Append("btcpay-terminal", terminalId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });

        return View("CheckedIn", new CheckedInViewModel
        {
            TerminalName = terminal.Name,
            TerminalId = terminal.TerminalId
        });
    }

    private async Task<Data.AppData> GetTerminalApp(string appId)
    {
        return await _appService.GetAppDataIfOwner(GetUserId(), appId, TerminalApp.AppType);
    }

    private string GetUserId() => User.GetId();

    private string GetAbsoluteUrl(string action, string terminalId)
    {
        var request = HttpContext.Request;
        var path = _linkGenerator.GetPathByAction(
            action, "UITerminal",
            new { terminalId },
            _btcPayOptions.Value.RootPath);
        return $"{request.Scheme}://{request.Host}{path}";
    }
}

public class TerminalSettingsViewModel
{
    public string AppId { get; set; }
    public string AppName { get; set; }
    public List<TerminalViewModel> Terminals { get; set; } = new();
}

public class TerminalViewModel
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string CurrentInvoiceId { get; set; }
    public bool IsActive { get; set; }
    public string PublicUrl { get; set; }
    public string CheckInUrl { get; set; }
}

public class WaitingViewModel
{
    public string TerminalName { get; set; }
    public string TerminalId { get; set; }
}

public class CheckedInViewModel
{
    public string TerminalName { get; set; }
    public string TerminalId { get; set; }
}
