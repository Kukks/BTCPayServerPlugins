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
        // erase the entire trust store - built up over possibly many quorum-signed rotations plus
        // any admin-added keys (see AddTrustedKey/RemoveTrustedKey below) - with no error, and no
        // way to notice until SecSwitch is re-enabled and immediately fails the "at least one
        // trusted key" check below, by which point the keys are already gone. This restore-before-
        // save guard is also a mass-assignment defence in its own right (Task 12 review, Finding
        // I4) - it must be preserved even now that TrustedKeys has real mutation endpoints, because
        // those endpoints mutate the PERSISTED record directly (see AddTrustedKey/RemoveTrustedKey)
        // rather than going through this form at all.
        var persisted = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        settings.TrustedKeys = persisted.TrustedKeys;
        settings.NotifyOnlyIdentifiers = persisted.NotifyOnlyIdentifiers;

        if (settings.QuorumThreshold < 1)
            ModelState.AddModelError(nameof(settings.QuorumThreshold), "Quorum must be at least 1.");

        // Final whole-branch review, Finding I2 (Important): FeedUrl was accepted unvalidated, and
        // AdvisoryFetcher refuses anything that is not absolute https by silently returning "nothing
        // fetched" (it has no logger, and never throws - by design). Typing "http://..." therefore
        // saved cleanly and left SecSwitch permanently, invisibly inert. Validated against
        // AdvisoryFetcher's OWN predicate rather than a re-stated copy, so the page can never accept
        // a URL the fetcher will not poll.
        if (!AdvisoryFetcher.IsSupportedFeedUrl(settings.FeedUrl))
            ModelState.AddModelError(nameof(settings.FeedUrl),
                "The advisory feed URL must be an absolute https:// URL. SecSwitch will not poll anything else, " +
                "and would silently fetch nothing if it were saved.");

        // Final whole-branch review, Finding I1 (Important): the guard was Count == 0, but a trust
        // store SMALLER THAN THE QUORUM is exactly as useless and far less obvious - quorum 2 with 1
        // key makes every advisory Unverified, which is excluded from both the bell notification and
        // the alert banner, so the settings page stays green and the key table stays populated while
        // the instance has no protection at all, indefinitely. This is the same invariant
        // TrustStore.TryApplyRotation already enforces for a quorum-signed rotation
        // (working.Count < Math.Max(1, quorumThreshold)); it was simply missing from the live admin
        // path, which is the one that actually runs. Uses the NEWLY submitted QuorumThreshold against
        // the persisted key count, so raising the quorum past the key count is refused too, not just
        // enabling with too few keys.
        var minimumKeys = Math.Max(1, settings.QuorumThreshold);
        if (settings.Enabled && settings.TrustedKeys.Count < minimumKeys)
            ModelState.AddModelError(nameof(settings.TrustedKeys),
                settings.TrustedKeys.Count == 0
                    ? "At least one trusted key is required before SecSwitch can verify anything."
                    : $"SecSwitch has only {settings.TrustedKeys.Count} trusted key(s) but requires {minimumKeys} " +
                      "signature(s) for quorum. Every advisory would fail verification and no action would ever " +
                      "be taken. Add more trusted keys, or lower the quorum, before enabling.");
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
        // write used) - the two can differ. The key is what SuppressAsync/UnsuppressAsync and every
        // other lookup in LedgerStore actually resolve against (case-insensitively), so it is the
        // only canonical identifier; this page and the Suppress/Unsuppress forms below must all use
        // it rather than the field.
        var rows = state.Entries
            .Select(kv => new AuditEntryRow(kv.Key, kv.Value))
            .OrderByDescending(row => row.Entry.RecordedAt)
            .ToList();
        return View(rows);
    }

    [HttpGet("suppress")]
    public async Task<IActionResult> SuppressConfirm(string advisoryId)
    {
        // Finding I3 (Task 12 review): an advisory id SecSwitch has never actually recorded must
        // never be suppressible - advisory ids come from the feed (attacker/publisher-influenced),
        // so accepting an unseen one here would let a typo'd or deliberately-chosen id pre-emptively
        // and permanently disarm an advisory that has not even been published yet. This GET-side
        // check only spares the admin a confirmation page for something that would be refused
        // anyway - LedgerStore.SuppressAsync applies the identical rule authoritatively when the
        // POST below actually mutates anything, so this is a UX nicety, not the enforcement point.
        if (string.IsNullOrWhiteSpace(advisoryId) || !await ledger.IsHandledAsync(advisoryId))
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"No advisory with id '{advisoryId}' has been seen; nothing to suppress.";
            return RedirectToAction(nameof(Audit));
        }

        return View("Confirm", new ConfirmActionViewModel
        {
            Title = "Suppress advisory",
            Description = $"Suppress advisory \"{advisoryId}\" on this instance? SecSwitch will not " +
                           "act on it again - not even automatically - until an admin un-suppresses " +
                           "it from the audit log.",
            FormAction = nameof(Suppress),
            FieldName = "advisoryId",
            FieldValue = advisoryId,
            ButtonLabel = "Suppress",
            CancelAction = nameof(Audit)
        });
    }

    [HttpPost("suppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suppress(string advisoryId)
    {
        if (!await ledger.SuppressAsync(advisoryId))
        {
            // Covers both a blank id and one LedgerStore has never recorded (see
            // LedgerStore.SuppressAsync's own doc comment, Finding I3) - the authoritative check;
            // SuppressConfirm above only tries to avoid showing this dialog in the common case.
            TempData[WellKnownTempData.ErrorMessage] =
                $"No advisory with id '{advisoryId}' has been seen; nothing to suppress.";
            return RedirectToAction(nameof(Audit));
        }

        TempData[WellKnownTempData.SuccessMessage] = $"Advisory {advisoryId} suppressed on this instance";
        return RedirectToAction(nameof(Audit));
    }

    [HttpGet("unsuppress")]
    public async Task<IActionResult> UnsuppressConfirm(string advisoryId)
    {
        var state = await ledger.GetAsync();
        if (string.IsNullOrWhiteSpace(advisoryId) ||
            !state.Entries.TryGetValue(advisoryId, out var entry) || !entry.Suppressed)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Advisory '{advisoryId}' is not currently suppressed.";
            return RedirectToAction(nameof(Audit));
        }

        return View("Confirm", new ConfirmActionViewModel
        {
            Title = "Un-suppress advisory",
            Description = $"Un-suppress advisory \"{advisoryId}\"? SecSwitch will re-evaluate it, and " +
                           "- if it still applies to this instance - may act on it automatically " +
                           "again (subject to your usual settings) the next time it polls the feed.",
            FormAction = nameof(Unsuppress),
            FieldName = "advisoryId",
            FieldValue = advisoryId,
            ButtonLabel = "Un-suppress",
            CancelAction = nameof(Audit)
        });
    }

    [HttpPost("unsuppress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsuppress(string advisoryId)
    {
        if (!await ledger.UnsuppressAsync(advisoryId))
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Advisory '{advisoryId}' is not currently suppressed.";
            return RedirectToAction(nameof(Audit));
        }

        TempData[WellKnownTempData.SuccessMessage] =
            $"Advisory {advisoryId} un-suppressed; it will be re-evaluated on the next poll.";
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

        // Finding I2 (Task 12 review): per the HTML form-submission algorithm, a <textarea>'s
        // submitted value has every line break normalised to CRLF by the BROWSER before it ever
        // reaches this action - regardless of what the admin actually pasted. A real advisory.json
        // signed with LF endings (the common case for anything produced by a typical Unix/git
        // toolchain) therefore arrives here as different bytes than the ones that were actually
        // signed, and a genuinely valid quorum would misreport as "Quorum NOT met" - on the one page
        // whose entire purpose is telling the admin whether to trust an advisory. Worst case: the
        // admin concludes a legitimate advisory is forged and suppresses it.
        //
        // Verify the bytes exactly as submitted first; only if THAT fails to meet quorum, retry with
        // CRLF normalised back to LF, and record on the model which variant (if either) actually
        // satisfied quorum, so the page states this explicitly rather than silently guessing a
        // newline convention either way. The real advisory-fetch path (AdvisoryFetcher) reads raw
        // HTTP response bytes directly and never goes through a textarea, so it is entirely
        // unaffected by any of this - the ambiguity exists solely because THIS page's input channel
        // is an HTML form control.
        var raw = model.AdvisoryJson ?? "";
        var asSubmitted = AdvisoryVerifier.Verify(
            Encoding.UTF8.GetBytes(raw), signatures, trusted, settings.QuorumThreshold);

        if (asSubmitted.QuorumMet)
        {
            model.Result = asSubmitted;
            model.MatchedNewlineVariant = "submitted bytes (unmodified)";
        }
        else
        {
            var normalized = raw.Replace("\r\n", "\n");
            var withLf = normalized == raw
                ? null // Nothing to retry - CRLF normalisation would not change a single byte.
                : AdvisoryVerifier.Verify(
                    Encoding.UTF8.GetBytes(normalized), signatures, trusted, settings.QuorumThreshold);

            if (withLf is { QuorumMet: true })
            {
                model.Result = withLf;
                model.MatchedNewlineVariant = "CRLF normalised to LF";
            }
            else
            {
                // Neither variant met quorum - show the as-submitted result (what the admin actually
                // typed) and leave MatchedNewlineVariant null so the view never claims a match.
                model.Result = asSubmitted;
                model.MatchedNewlineVariant = null;
            }
        }

        model.TrustedFingerprints = settings.TrustedKeys.Select(k => k.Fingerprint).ToList();
        return View(model);
    }

    [HttpPost("trusted-keys/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTrustedKey(string armoredPublicKey)
    {
        if (string.IsNullOrWhiteSpace(armoredPublicKey))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Paste an armored public key first.";
            return RedirectToAction(nameof(Settings));
        }

        string fingerprint;
        try
        {
            // Finding I4 (Task 12 review): the fingerprint is ALWAYS re-derived from the pasted key
            // material itself, never accepted as typed text - see TrustedKey's own doc comment for
            // why an admin-typed-but-mismatched fingerprint would silently contribute nothing to
            // quorum (AdvisoryVerifier cross-checks each trusted entry against its dictionary
            // label). FingerprintOf throws on anything it cannot parse as a usable OpenPGP key ring
            // - this endpoint exists specifically to accept an admin's raw paste, which is exactly
            // the adversarial/malformed input that must fail closed with a validation error, never a
            // 500.
            fingerprint = AdvisoryVerifier.FingerprintOf(armoredPublicKey);
        }
        catch (Exception)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "That does not look like a valid armored PGP public key; nothing was added.";
            return RedirectToAction(nameof(Settings));
        }

        // Beyond FingerprintOf's own guard: also require the SAME admission check
        // AdvisoryVerifier.LoadTrustedKeys applies before a trusted entry can ever actually count
        // toward quorum (TryLoadTrustedKey - shared with TrustStore.TryApplyRotation specifically so
        // the two can never drift apart, see its own doc comment). A blob FingerprintOf can parse is
        // not necessarily one LoadTrustedKeys would ever load (e.g. one over its own byte-length
        // cap) - skipping this would let an admin "successfully" add a key that silently
        // contributes nothing to quorum forever, with no error to explain why.
        if (!AdvisoryVerifier.TryLoadTrustedKey(fingerprint, armoredPublicKey, out var matchedRing) ||
            matchedRing is null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"That key was readable but SecSwitch could never actually load it as trusted " +
                $"(fingerprint {fingerprint}); nothing was added.";
            return RedirectToAction(nameof(Settings));
        }

        // Final whole-branch review, Finding I3 (Important): screen the key for revocation and expiry,
        // exactly as TrustStore.TryApplyRotation already does for a key arriving via a quorum-signed
        // rotation. AdvisoryVerifier.Verify deliberately performs neither check, so a revoked or
        // expired signer counts toward quorum there - which means this admission point is the only
        // place a revoked key can be kept out at all, and it was the one path missing the check. Same
        // limits as the rotation-side copy: IsRevoked() is packet-presence only (BouncyCastle does not
        // cryptographically validate the revocation signature it finds), so this catches an honest or
        // keyserver-sourced revoked blob and lets a hostile paste force a refusal - fail-closed, a
        // false refuse rather than a false accept. All three BouncyCastle calls share one try/catch:
        // GetValidSeconds() walks self-certification subpackets, the same class of parsing that can
        // throw on hostile input, and a key whose status cannot be determined at all must be refused
        // rather than silently admitted.
        bool isRevoked;
        DateTime? expiresAt;
        try
        {
            var primaryKey = matchedRing.GetPublicKey();
            isRevoked = primaryKey.IsRevoked();
            // GetValidSeconds() == 0 means "no expiry", per OpenPGP (RFC 4880) and BouncyCastle's own
            // documented contract on PgpPublicKey.GetValidSeconds().
            var validSeconds = primaryKey.GetValidSeconds();
            expiresAt = validSeconds == 0 ? null : primaryKey.CreationTime.AddSeconds(validSeconds);
        }
        catch (Exception)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"SecSwitch could not determine whether that key ({fingerprint}) is revoked or expired; " +
                "nothing was added.";
            return RedirectToAction(nameof(Settings));
        }

        if (isRevoked)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"That key ({fingerprint}) carries a revocation; refusing to trust it.";
            return RedirectToAction(nameof(Settings));
        }

        if (expiresAt is not null && expiresAt <= DateTime.UtcNow)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"That key ({fingerprint}) expired on {expiresAt:u}; refusing to trust it.";
            return RedirectToAction(nameof(Settings));
        }

        var persisted = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        if (persisted.TrustedKeys.Any(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            TempData[WellKnownTempData.ErrorMessage] = $"A key with fingerprint {fingerprint} is already trusted.";
            return RedirectToAction(nameof(Settings));
        }

        persisted.TrustedKeys.Add(new TrustedKey
        { Fingerprint = fingerprint, ArmoredPublicKey = armoredPublicKey, Identity = fingerprint });
        await settingsRepository.UpdateSetting(persisted);
        TempData[WellKnownTempData.SuccessMessage] = $"Trusted key {fingerprint} added.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("trusted-keys/remove")]
    public async Task<IActionResult> RemoveTrustedKeyConfirm(string fingerprint)
    {
        var persisted = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        if (string.IsNullOrWhiteSpace(fingerprint) ||
            !persisted.TrustedKeys.Any(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            TempData[WellKnownTempData.ErrorMessage] = "No trusted key with that fingerprint.";
            return RedirectToAction(nameof(Settings));
        }

        return View("Confirm", new ConfirmActionViewModel
        {
            Title = "Remove trusted key",
            Description = $"Remove trusted key \"{fingerprint}\"? A future advisory or key rotation " +
                           "may need this key to reach quorum - removing it can reduce SecSwitch's " +
                           "ability to verify anything until it is re-added, or a new quorum-signed " +
                           "rotation restores it.",
            FormAction = nameof(RemoveTrustedKey),
            FieldName = "fingerprint",
            FieldValue = fingerprint,
            ButtonLabel = "Remove",
            CancelAction = nameof(Settings)
        });
    }

    [HttpPost("trusted-keys/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveTrustedKey(string fingerprint)
    {
        var persisted = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
        var matches = persisted.TrustedKeys.Count(
            k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        if (matches == 0)
        {
            TempData[WellKnownTempData.ErrorMessage] = "No trusted key with that fingerprint.";
            return RedirectToAction(nameof(Settings));
        }

        // Final whole-branch review, Finding I1 (Important): this endpoint had NO floor guard at all -
        // an admin could remove keys one at a time until the store held fewer than the quorum
        // threshold (or nothing at all) while SecSwitch stayed enabled, at which point every advisory
        // becomes Unverified: excluded from notifications AND from the alert banner, so the instance
        // looks protected and is not. Same invariant TrustStore.TryApplyRotation enforces for a
        // rotation (working.Count < Math.Max(1, quorumThreshold)), applied here to the live admin
        // path. Computed against what the store WOULD hold after this removal, not what it holds now.
        //
        // Scoped to Enabled deliberately: while SecSwitch is switched off nothing is being protected,
        // so an admin must still be able to clear out a mistakenly-added key - including the very
        // first one, which an unconditional floor would make permanently unremovable. Re-enabling then
        // runs the Settings POST guard above, which refuses to turn SecSwitch back on until the store
        // is at or above quorum again, so the two guards together leave no window in which an ENABLED
        // SecSwitch sits below its own quorum.
        var minimumKeys = Math.Max(1, persisted.QuorumThreshold);
        if (persisted.Enabled && persisted.TrustedKeys.Count - matches < minimumKeys)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                $"Removing that key would leave {persisted.TrustedKeys.Count - matches} trusted key(s), below " +
                $"the quorum threshold of {minimumKeys}. Every advisory would then fail verification and " +
                "SecSwitch would silently stop protecting this instance. Lower the quorum, add another key, " +
                "or disable SecSwitch first.";
            return RedirectToAction(nameof(Settings));
        }

        persisted.TrustedKeys.RemoveAll(
            k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        await settingsRepository.UpdateSetting(persisted);
        TempData[WellKnownTempData.SuccessMessage] = $"Trusted key {fingerprint} removed.";
        return RedirectToAction(nameof(Settings));
    }

    /// <summary>
    /// Pairs a ledger entry with its canonical (dictionary-key) advisory id for display - see the
    /// doc comment on <see cref="Audit"/> for why the key, not <see cref="LedgerEntry.AdvisoryId"/>,
    /// is the one to render and to post back to <see cref="Suppress"/>/<see cref="Unsuppress"/>.
    /// </summary>
    public sealed record AuditEntryRow(string AdvisoryId, LedgerEntry Entry);

    public class VerifyViewModel
    {
        public string? AdvisoryJson { get; set; }
        public string? ArmoredSignatures { get; set; }
        public VerificationResult? Result { get; set; }
        public List<string> TrustedFingerprints { get; set; } = [];

        /// <summary>
        /// Which newline form of <see cref="AdvisoryJson"/> actually satisfied quorum (Finding I2's
        /// fix in <see cref="Verify(VerifyViewModel)"/>) - "submitted bytes (unmodified)", "CRLF
        /// normalised to LF", or null if neither did (in which case <see cref="Result"/>'s
        /// QuorumMet is false regardless) or no verification has run yet.
        /// </summary>
        public string? MatchedNewlineVariant { get; set; }
    }

    /// <summary>
    /// A minimal, self-contained "are you sure?" page model for a destructive action (Task 12
    /// review, Findings I3 and I4): posts a single hidden field back to <see cref="FormAction"/> on
    /// THIS controller, via the exact same "asp-action + hidden field" form idiom every other
    /// state-changing view in this plugin already used pre-review (see the audit log's pre-existing
    /// Suppress button) - already proven to carry the antiforgery token the target action's
    /// [ValidateAntiForgeryToken] requires, via the standard MVC form tag helper.
    ///
    /// Deliberately NOT BTCPayServer core's shared ConfirmModel/Confirm.cshtml modal
    /// (Views/Shared/Confirm.cshtml, BTCPayServer.Abstractions.Models.ConfirmModel): that partial
    /// only emits an antiforgery-protected form when its own Antiforgery flag is explicitly opted
    /// into by the caller (its default renders a plain &lt;form action="..."&gt; with no asp-*
    /// attribute at all, so ASP.NET Core's form tag helper - and the antiforgery token it injects -
    /// never engages at all) - a real risk of silently shipping an unprotected confirmation form for
    /// a security-relevant action, and not something this plugin's compile-time-only view
    /// verification (see task-12-report.md) would catch. This local, always-CSRF-protected shape
    /// removes that risk entirely rather than depending on remembering to opt in correctly.
    /// </summary>
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
