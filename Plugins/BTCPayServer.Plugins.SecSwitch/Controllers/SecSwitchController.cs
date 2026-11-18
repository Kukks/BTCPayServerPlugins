using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("~/plugins/secswitch")]
public class SecSwitchController(
    LedgerStore ledger,
    LockdownGate lockdown,
    ILogger<SecSwitchController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Settings()
    {
        if (await lockdown.IsActiveAsync())
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var settings = await ledger.GetPolicyAsync();
        settings.FeedUrl = settings.GetFeedUrl();
        return View(settings);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(SecSwitchSettings settings)
    {
        if (await lockdown.IsActiveAsync())
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

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
        var completed = false;
        var killSwitchEnabled = settings.Enabled && settings.AutoApply;
        try
        {
            var allowed = await ledger.WithPolicyAsync(async previous =>
            {
                if (await HasActiveLockdownAsync(previous))
                {
                    await lockdown.ActivateAsync();
                    return false;
                }

                if (killSwitchEnabled && (!previous.Enabled || !previous.AutoApply))
                    await ledger.ClearLocksAsync("Cleared an earlier lockdown before re-enabling the kill switch.");

                settings.PolicyRevision = Guid.NewGuid();
                await ledger.SavePolicyAsync(settings);
                saved = true;

                if (!killSwitchEnabled)
                    await ledger.ClearLocksAsync("The server admin turned off advisory checks or the kill switch.");
                return true;
            });
            if (!allowed)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            completed = true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not complete the settings change");
            TempData[WellKnownTempData.ErrorMessage] = saved
                ? "Settings saved and the kill switch is off. SecSwitch could not clear the previous lockdown record."
                : "Could not save SecSwitch settings.";
        }
        if (await lockdown.RefreshAsync())
        {
            if (completed)
                TempData[WellKnownTempData.SuccessMessage] = "SecSwitch settings saved";
        }
        else if (completed)
            TempData[WellKnownTempData.ErrorMessage] = "Settings saved. SecSwitch could not refresh the lockdown status and will retry automatically.";

        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
    {
        ViewData["SecSwitchRecoveryMode"] = await lockdown.IsActiveAsync();
        return View((await ledger.GetAsync()).Entries.Values.OrderByDescending(e => e.RecordedAt).ToList());
    }

    [HttpGet("suppress")]
    public Task<IActionResult> SuppressConfirm(string advisoryId) => ConfirmAsync(advisoryId, suppress: true);

    [HttpGet("unsuppress")]
    public async Task<IActionResult> UnsuppressConfirm(string advisoryId) => await lockdown.IsActiveAsync()
        ? StatusCode(StatusCodes.Status503ServiceUnavailable)
        : await ConfirmAsync(advisoryId, suppress: false);

    [HttpPost("suppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suppress(string advisoryId, bool acknowledgeRisk = false)
    {
        if (!acknowledgeRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Acknowledge the warning before suppressing this advisory.";
            return RedirectToAction(nameof(SuppressConfirm), new { advisoryId });
        }

        var done = await ledger.WithPolicyAsync(_ => ledger.SuppressAsync(advisoryId));
        if (!await lockdown.RefreshAsync() && done)
            return Report(false, "", "Advisory suppressed. SecSwitch could not refresh the lockdown status and will retry automatically.");
        return Report(done, $"Advisory {advisoryId} suppressed on this instance.", $"No advisory '{advisoryId}' to suppress.");
    }

    [HttpPost("unsuppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsuppress(string advisoryId)
    {
        var done = await ledger.WithPolicyAsync<bool?>(async settings =>
            await HasActiveLockdownAsync(settings) ? null : await ledger.UnsuppressAsync(advisoryId));
        if (done is not { } succeeded)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        return Report(succeeded, $"Advisory '{advisoryId}' un-suppressed. SecSwitch will check it on the next poll.",
            $"Advisory '{advisoryId}' is not currently suppressed.");
    }

    [HttpPost("update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update([FromServices] SecSwitchMonitor monitor, CancellationToken cancellationToken)
    {
        var outcome = await monitor.RecoverWithCoreUpdateAsync(cancellationToken);
        return Report(outcome.Succeeded, outcome.Message, outcome.Message);
    }

    async Task<bool> HasActiveLockdownAsync(SecSwitchSettings settings) =>
        settings is { Enabled: true, AutoApply: true } && LockdownGate.HasActiveLockdown(settings, await ledger.GetAsync());

    async Task<IActionResult> ConfirmAsync(string advisoryId, bool suppress)
    {
        if (!(await ledger.GetAsync()).Entries.TryGetValue(advisoryId ?? "", out var entry) || entry.Suppressed == suppress)
            return Report(false, "", suppress
                ? $"No advisory '{advisoryId}' to suppress."
                : $"Advisory '{advisoryId}' is not currently suppressed.");

        ViewData["SecSwitchRecoveryMode"] = await lockdown.IsActiveAsync();
        return View("Confirm", new ConfirmActionViewModel
        {
            Title = suppress ? "Suppress advisory" : "Un-suppress advisory",
            Description = suppress
                ? $"Suppress advisory \"{entry.AdvisoryId}\" on this instance?"
                : $"Un-suppress advisory \"{advisoryId}\"? SecSwitch will evaluate it again on the next poll and, if it " +
                  "still applies, act on it as your settings dictate.",
            FormAction = suppress ? nameof(Suppress) : nameof(Unsuppress),
            FieldName = "advisoryId",
            FieldValue = entry.AdvisoryId,
            ButtonLabel = suppress ? "Suppress advisory" : "Un-suppress",
            CancelAction = nameof(Audit),
            RequireAcknowledgement = suppress
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
        public bool RequireAcknowledgement { get; set; }
    }
}
