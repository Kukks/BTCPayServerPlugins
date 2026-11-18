using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("~/plugins/secswitch")]
public class SecSwitchController(
    ISettingsRepository settingsRepository,
    LedgerStore ledger,
    LockdownGate lockdown,
    ILogger<SecSwitchController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Settings()
    {
        var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        settings.FeedUrl = settings.GetFeedUrl();
        return View(settings);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(SecSwitchSettings settings)
    {
        var feedUrl = settings.GetFeedUrl();
        if (settings.HasLegacyPlaceholderFeedUrl())
            ModelState.AddModelError(nameof(settings.FeedUrl), "This placeholder URL has been retired. Enter a trusted advisory feed URL or leave the field blank.");
        else if (feedUrl is not null && !AdvisoryFeed.IsSupportedFeedUrl(feedUrl))
            ModelState.AddModelError(nameof(settings.FeedUrl), "The advisory feed URL must be an absolute https:// URL.");
        if (!ModelState.IsValid)
            return View(settings);

        settings.FeedUrl = feedUrl;
        settings.AutoUpdatePlugins = (settings.AutoUpdatePlugins ?? [])
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var saved = false;
        var killSwitchEnabled = settings.Enabled && settings.AutoApply;
        try
        {
            await ledger.WithPolicyAsync(async previous =>
            {
                if (killSwitchEnabled && (!previous.Enabled || !previous.AutoApply))
                    await ledger.ClearLocksAsync("Cleared an earlier lockdown before re-enabling the kill switch.");

                settings.PolicyRevision = Guid.NewGuid();
                await settingsRepository.UpdateSetting(settings);
                saved = true;

                if (!killSwitchEnabled)
                    await ledger.ClearLocksAsync("The server admin turned off advisory checks or the kill switch.");
                return true;
            });
            TempData[WellKnownTempData.SuccessMessage] = "SecSwitch settings saved";
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not complete the settings change");
            TempData[WellKnownTempData.ErrorMessage] = saved
                ? "Settings saved and the kill switch is off. SecSwitch could not clear the previous lockdown record."
                : "Could not save SecSwitch settings.";
        }
        finally
        {
            await lockdown.RefreshAsync();
        }

        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
        => View((await ledger.GetAsync()).Entries.Values.OrderByDescending(e => e.RecordedAt).ToList());

    [HttpGet("suppress")]
    public Task<IActionResult> SuppressConfirm(string advisoryId) => ConfirmAsync(advisoryId, suppress: true);

    [HttpGet("unsuppress")]
    public Task<IActionResult> UnsuppressConfirm(string advisoryId) => ConfirmAsync(advisoryId, suppress: false);

    [HttpPost("suppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suppress(string advisoryId)
    {
        var done = await ledger.SuppressAsync(advisoryId);
        await lockdown.RefreshAsync();
        return Report(done, $"Advisory {advisoryId} suppressed on this instance.", $"No advisory '{advisoryId}' to suppress.");
    }

    [HttpPost("unsuppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsuppress(string advisoryId)
    {
        var done = await ledger.UnsuppressAsync(advisoryId);
        return Report(done, $"Advisory {advisoryId} un-suppressed; it will be evaluated again on the next poll.",
            $"Advisory '{advisoryId}' is not currently suppressed.");
    }

    async Task<IActionResult> ConfirmAsync(string advisoryId, bool suppress)
    {
        var found = (await ledger.GetAsync()).Entries.TryGetValue(advisoryId ?? "", out var entry);
        if (!found || entry!.Suppressed == suppress)
            return Report(false, "", suppress
                ? $"No advisory '{advisoryId}' to suppress."
                : $"Advisory '{advisoryId}' is not currently suppressed.");

        return View("Confirm", new ConfirmActionViewModel
        {
            Title = suppress ? "Suppress advisory" : "Un-suppress advisory",
            Description = suppress
                ? $"Suppress advisory \"{advisoryId}\" on this instance? SecSwitch will not act on it again until you " +
                  "un-suppress it. If it is holding this server in lockdown, suppressing it lifts the lockdown."
                : $"Un-suppress advisory \"{advisoryId}\"? SecSwitch will evaluate it again on the next poll and, if it " +
                  "still applies, act on it as your settings dictate.",
            FormAction = suppress ? nameof(Suppress) : nameof(Unsuppress),
            FieldName = "advisoryId",
            FieldValue = advisoryId!,
            ButtonLabel = suppress ? "Suppress" : "Un-suppress",
            CancelAction = nameof(Audit)
        });
    }

    IActionResult Report(bool success, string successMessage, string errorMessage)
    {
        TempData[success ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = success ? successMessage : errorMessage;
        return RedirectToAction(nameof(Audit));
    }

    public sealed class ConfirmActionViewModel
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string FormAction { get; set; } = "";
        public string FieldName { get; set; } = "";
        public string FieldValue { get; set; } = "";
        public string ButtonLabel { get; set; } = "Confirm";
        public string CancelAction { get; set; } = "";
    }
}
