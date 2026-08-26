using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.SecSwitch.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("~/plugins/secswitch")]
public class SecSwitchController(ISettingsRepository settingsRepository, LedgerStore ledger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Settings()
        => View(await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings());

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(SecSwitchSettings settings)
    {
        // This view only ever posts the general toggles below - it carries no per-key inputs for
        // TrustedKeys or NotifyOnlyIdentifiers, so the default model binder would otherwise leave
        // both collections at their empty field-initializer value on every submit. Restoring them
        // from the persisted record BEFORE validating/saving is required, not cosmetic: without
        // it, saving any unrelated toggle (e.g. FeedUrl) while SecSwitch is disabled would silently
        // erase the entire trust store - built up over possibly many quorum-signed rotations (see
        // TrustStore) - with no error, and no way to notice until SecSwitch is re-enabled and
        // immediately fails the "at least one trusted key" check below, by which point the keys
        // are already gone. TrustedKey.Fingerprint is only ever produced by
        // AdvisoryVerifier.FingerprintOf via TrustStore's quorum-signed rotation (see
        // Models/TrustedKey.cs) - this page deliberately has no "add a key" form of its own, so
        // there is nothing for an admin to type a fingerprint into here in the first place.
        var persisted = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        settings.TrustedKeys = persisted.TrustedKeys;
        settings.NotifyOnlyIdentifiers = persisted.NotifyOnlyIdentifiers;

        if (settings.QuorumThreshold < 1)
            ModelState.AddModelError(nameof(settings.QuorumThreshold), "Quorum must be at least 1.");
        if (settings.Enabled && settings.TrustedKeys.Count == 0)
            ModelState.AddModelError(nameof(settings.TrustedKeys),
                "At least one trusted key is required before SecSwitch can verify anything.");
        if (!ModelState.IsValid)
            return View(settings);

        await settingsRepository.UpdateSetting(settings);
        TempData[WellKnownTempData.SuccessMessage] = "SecSwitch settings saved";
        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
    {
        var state = await ledger.GetAsync();

        // Keyed off the dictionary key, not entry.Value.AdvisoryId: LedgerStore.RecordAsync
        // upserts via `ledger.Entries[entry.AdvisoryId] = entry`, so the KEY is first-write-wins
        // (whatever casing the advisory id had the first time it was ever recorded) while the
        // AdvisoryId FIELD on the entry itself is last-write-wins (whatever casing the most recent
        // write used) - the two can differ. The key is what SuppressAsync and every other lookup
        // in LedgerStore actually resolves against (case-insensitively), so it is the only
        // canonical identifier; this page and the Suppress form below must both use it rather than
        // the field.
        var rows = state.Entries
            .Select(kv => new AuditEntryRow(kv.Key, kv.Value))
            .OrderByDescending(row => row.Entry.RecordedAt)
            .ToList();
        return View(rows);
    }

    [HttpPost("suppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suppress(string advisoryId)
    {
        await ledger.SuppressAsync(advisoryId);
        TempData[WellKnownTempData.SuccessMessage] = $"Advisory {advisoryId} suppressed on this instance";
        return RedirectToAction(nameof(Audit));
    }

    [HttpGet("verify")]
    public IActionResult Verify() => View(new VerifyViewModel());

    [HttpPost("verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(VerifyViewModel model)
    {
        var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();

        // Built defensively rather than via a raw ToDictionary - mirrors SecSwitchMonitor.ProcessAsync's
        // own established pattern for this exact conversion (see its doc comment): a naive
        // ToDictionary throws ArgumentException the instant two trusted keys share a fingerprint
        // (case-insensitively), which would 500 this page instead of rendering a result. This page
        // exists specifically to safely inspect adversarial/untrusted input, so it must not be the
        // one page in the plugin that can be crashed by a corrupt settings row.
        var trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in settings.TrustedKeys)
        {
            if (key is null || string.IsNullOrWhiteSpace(key.Fingerprint) || key.ArmoredPublicKey is null)
                continue;
            trusted[key.Fingerprint] = key.ArmoredPublicKey;
        }

        var signatures = (model.ArmoredSignatures ?? "")
            .Split("-----END PGP SIGNATURE-----", StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s + "-----END PGP SIGNATURE-----")
            .Where(s => s.Contains("BEGIN PGP SIGNATURE"))
            .ToList();

        model.Result = AdvisoryVerifier.Verify(
            Encoding.UTF8.GetBytes(model.AdvisoryJson ?? ""), signatures, trusted, settings.QuorumThreshold);
        model.TrustedFingerprints = settings.TrustedKeys.Select(k => k.Fingerprint).ToList();
        return View(model);
    }

    /// <summary>
    /// Pairs a ledger entry with its canonical (dictionary-key) advisory id for display - see the
    /// doc comment on <see cref="Audit"/> for why the key, not <see cref="LedgerEntry.AdvisoryId"/>,
    /// is the one to render and to post back to <see cref="Suppress"/>.
    /// </summary>
    public sealed record AuditEntryRow(string AdvisoryId, LedgerEntry Entry);

    public class VerifyViewModel
    {
        public string? AdvisoryJson { get; set; }
        public string? ArmoredSignatures { get; set; }
        public VerificationResult? Result { get; set; }
        public List<string> TrustedFingerprints { get; set; } = [];
    }
}
