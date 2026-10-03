using System.Text;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Controllers;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// Functional (not just reflection-based) tests for the controller logic Task 12's review findings
/// added or changed: Verify's newline-retry branching (Finding I2), Suppress/Unsuppress's
/// reject-unknown-id and confirm-first flows (Finding I3), and AddTrustedKey/RemoveTrustedKey's
/// validation and mutation (Finding I4). FakeSettingsRepository is reused from LedgerStoreTests.cs
/// (same test assembly/namespace).
public class SecSwitchControllerTests
{
    /// A no-op ITempDataProvider so SecSwitchController.TempData can be assigned a real
    /// TempDataDictionary without needing a full HttpContext.RequestServices graph.
    sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    static SecSwitchController MakeController(ISettingsRepository repo, LedgerStore ledger)
    {
        var controller = new SecSwitchController(repo, ledger)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };
        return controller;
    }

    static LedgerEntry Entry(string id, string status = LedgerStatus.Acted) => new()
    {
        AdvisoryId = id, Title = "t", Identifier = "Plug", Severity = "High",
        Status = status, Action = "UpdatePlugin", Reason = "r", RecordedAt = DateTimeOffset.UtcNow
    };

    static TrustedKey Key(PgpTestKey k) => new()
    { Fingerprint = k.Fingerprint, ArmoredPublicKey = k.ArmoredPublicKey, Identity = k.Fingerprint };

    // --- Finding I3: Suppress must reject an id LedgerStore has never recorded, and confirm first. ---

    [Fact]
    public async Task SuppressConfirm_redirects_with_an_error_for_an_unknown_advisory_id()
    {
        var repo = new FakeSettingsRepository();
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.SuppressConfirm("never-seen");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Audit), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
    }

    [Fact]
    public async Task SuppressConfirm_shows_a_confirmation_page_for_a_known_advisory_id()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        await ledger.RecordAsync(Entry("a1"));
        var controller = MakeController(repo, ledger);

        var result = await controller.SuppressConfirm("a1");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Confirm", view.ViewName);
        var model = Assert.IsType<SecSwitchController.ConfirmActionViewModel>(view.Model);
        Assert.Equal("a1", model.FieldValue);
        Assert.Equal(nameof(SecSwitchController.Suppress), model.FormAction);
    }

    [Fact]
    public async Task Suppress_rejects_an_unknown_advisory_id_and_creates_nothing()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        var controller = MakeController(repo, ledger);

        var result = await controller.Suppress("never-seen");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Audit), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        Assert.Empty((await ledger.GetAsync()).Entries); // Finding I3: never fabricated
    }

    [Fact]
    public async Task Suppress_succeeds_for_a_recorded_advisory()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        await ledger.RecordAsync(Entry("a1"));
        var controller = MakeController(repo, ledger);

        var result = await controller.Suppress("a1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Audit), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        Assert.True((await ledger.GetAsync()).Entries["a1"].Suppressed);
    }

    // --- Finding I3: Unsuppress must reject an id that is not currently suppressed, and confirm first. ---

    [Fact]
    public async Task UnsuppressConfirm_redirects_with_an_error_when_not_currently_suppressed()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        await ledger.RecordAsync(Entry("a1")); // recorded, but never suppressed
        var controller = MakeController(repo, ledger);

        var result = await controller.UnsuppressConfirm("a1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Audit), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
    }

    [Fact]
    public async Task UnsuppressConfirm_shows_a_confirmation_page_for_a_suppressed_advisory()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        await ledger.RecordAsync(Entry("a1"));
        await ledger.SuppressAsync("a1");
        var controller = MakeController(repo, ledger);

        var result = await controller.UnsuppressConfirm("a1");

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Confirm", view.ViewName);
        var model = Assert.IsType<SecSwitchController.ConfirmActionViewModel>(view.Model);
        Assert.Equal("a1", model.FieldValue);
        Assert.Equal(nameof(SecSwitchController.Unsuppress), model.FormAction);
    }

    [Fact]
    public async Task Unsuppress_clears_suppressed_status_and_content_hash()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        var entry = Entry("a1");
        entry.ContentHash = "hash-a1";
        await ledger.RecordAsync(entry);
        await ledger.SuppressAsync("a1");
        var controller = MakeController(repo, ledger);

        var result = await controller.Unsuppress("a1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Audit), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var final = (await ledger.GetAsync()).Entries["a1"];
        Assert.False(final.Suppressed);
        Assert.Equal(LedgerStatus.Unsuppressed, final.Status);
        Assert.Equal("", final.ContentHash);
    }

    [Fact]
    public async Task Unsuppress_rejects_an_advisory_that_is_not_currently_suppressed()
    {
        var repo = new FakeSettingsRepository();
        var ledger = new LedgerStore(repo);
        await ledger.RecordAsync(Entry("a1"));
        var controller = MakeController(repo, ledger);

        var result = await controller.Unsuppress("a1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Audit), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
    }

    // --- Finding I2: Verify must retry with CRLF normalised to LF, and say which variant matched. ---

    [Fact]
    public async Task Verify_falls_back_to_CRLF_normalised_bytes_when_the_raw_submission_misses_quorum()
    {
        // Simulates the exact failure Finding I2 describes: a real advisory.json signed with LF
        // endings, submitted through a <textarea> that (per the HTML form-submission algorithm)
        // normalises every line break to CRLF before this action ever sees it.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        const string lfJson = "{\n  \"id\": \"a1\"\n}";
        var lfPayload = Encoding.UTF8.GetBytes(lfJson);
        var sigA = a.SignDetached(lfPayload);
        var sigB = b.SignDetached(lfPayload);

        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { QuorumThreshold = 2, TrustedKeys = [Key(a), Key(b)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var crlfJson = lfJson.Replace("\n", "\r\n"); // what the browser actually submits
        var result = await controller.Verify(new SecSwitchController.VerifyViewModel
        {
            AdvisoryJson = crlfJson,
            ArmoredSignatures = sigA + "\n" + sigB
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SecSwitchController.VerifyViewModel>(view.Model);
        Assert.NotNull(model.Result);
        Assert.True(model.Result!.QuorumMet);
        Assert.Equal("CRLF normalised to LF", model.MatchedNewlineVariant);
    }

    [Fact]
    public async Task Verify_reports_the_submitted_bytes_variant_when_they_already_meet_quorum()
    {
        // The advisory was signed over exactly the bytes submitted (no CRLF/LF mismatch at all) -
        // the as-submitted attempt must succeed on its own, with no fallback needed.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        const string json = "{\"id\":\"a1\"}"; // single line - no newlines to normalise either way
        var payload = Encoding.UTF8.GetBytes(json);
        var sigA = a.SignDetached(payload);
        var sigB = b.SignDetached(payload);

        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { QuorumThreshold = 2, TrustedKeys = [Key(a), Key(b)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Verify(new SecSwitchController.VerifyViewModel
        {
            AdvisoryJson = json,
            ArmoredSignatures = sigA + "\n" + sigB
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SecSwitchController.VerifyViewModel>(view.Model);
        Assert.True(model.Result!.QuorumMet);
        Assert.Equal("submitted bytes (unmodified)", model.MatchedNewlineVariant);
    }

    [Fact]
    public async Task Verify_reports_no_matched_variant_when_neither_form_meets_quorum()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { QuorumThreshold = 2, TrustedKeys = [Key(a), Key(b)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Verify(new SecSwitchController.VerifyViewModel
        {
            AdvisoryJson = "{\"id\":\"unsigned\"}",
            ArmoredSignatures = ""
        });

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SecSwitchController.VerifyViewModel>(view.Model);
        Assert.False(model.Result!.QuorumMet);
        Assert.Null(model.MatchedNewlineVariant);
    }

    [Fact]
    public async Task Verify_does_not_throw_when_a_persisted_trusted_key_is_null()
    {
        // PR #151 review (CodeRabbit), Finding F1: a persisted settings row whose trustedKeys array
        // contains a JSON `null` deserializes to a List<TrustedKey> with a null element. The
        // `trusted` dictionary built earlier in Verify already guards against this (see its own
        // `key is null` check), but TrustedFingerprints was projected straight off
        // settings.TrustedKeys with no such guard, so `k.Fingerprint` NREd on the null element - a
        // 500 from the one page whose purpose is to inspect input safely even when the settings row
        // itself is corrupt.
        var a = PgpTestKeys.Generate("a@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { QuorumThreshold = 2, TrustedKeys = [Key(a), null!] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Verify(new SecSwitchController.VerifyViewModel
        {
            AdvisoryJson = "{\"id\":\"a1\"}",
            ArmoredSignatures = ""
        });

        var view = Assert.IsType<ViewResult>(result); // must not throw
        var model = Assert.IsType<SecSwitchController.VerifyViewModel>(view.Model);
        Assert.Contains(a.Fingerprint, model.TrustedFingerprints); // the non-null key still comes through
    }

    // --- Finding I4: AddTrustedKey must never accept a typed fingerprint, must fail closed (not
    // 500) on malformed input, and must reject a key AdvisoryVerifier could never actually load. ---

    [Fact]
    public async Task AddTrustedKey_rejects_malformed_input_without_throwing()
    {
        var repo = new FakeSettingsRepository();
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey("this is not an armored PGP key at all");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Settings), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        var settings = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.True(settings is null || settings.TrustedKeys.Count == 0);
    }

    [Fact]
    public async Task AddTrustedKey_derives_the_fingerprint_from_the_key_material_and_persists_it()
    {
        var key = PgpTestKeys.Generate("bootstrap@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings());
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey(key.ArmoredPublicKey);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Settings), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var settings = await repo.GetSettingAsync<SecSwitchSettings>();
        var added = Assert.Single(settings!.TrustedKeys);
        Assert.Equal(key.Fingerprint, added.Fingerprint);
        Assert.Equal(key.ArmoredPublicKey, added.ArmoredPublicKey);
    }

    [Fact]
    public async Task AddTrustedKey_rejects_a_duplicate_fingerprint()
    {
        var key = PgpTestKeys.Generate("dup@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { TrustedKeys = [Key(key)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey(key.ArmoredPublicKey);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Settings), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        var settings = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Single(settings!.TrustedKeys); // not duplicated
    }

    [Fact]
    public async Task AddTrustedKey_rejects_a_key_AdvisoryVerifier_could_never_actually_load()
    {
        // Beyond the literal finding: FingerprintOf has no byte-length cap, but
        // AdvisoryVerifier.LoadTrustedKeys (via TryLoadTrustedKey) refuses anything over
        // MaxTrustedKeyBlobLength (256 KiB) - admitting such a key here would silently add a
        // "trusted" entry that can never contribute to quorum. Padding filler AFTER a complete,
        // genuine armor block is inert to parsing (armor decoding stops at the END marker) but
        // still counts toward the raw byte-length cap - the exact same construction
        // TrustStoreTests.Rotation_refuses_to_add_a_key_that_AdvisoryVerifier_would_later_refuse_to_load
        // already uses for the equivalent rotation-side check.
        var oversized = PgpTestKeys.Generate("bloated@x");
        var bloatedArmoredKey = oversized.ArmoredPublicKey + new string('X', 256 * 1024);
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings());
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey(bloatedArmoredKey);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Settings), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        var settings = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Empty(settings!.TrustedKeys);
    }

    [Fact]
    public async Task AddTrustedKey_RemoveTrustedKeyConfirm_and_RemoveTrustedKey_do_not_throw_when_a_persisted_trusted_key_is_null()
    {
        // Bonus, adjacent to Finding F1 (not itself one of the CodeRabbit PR #151 findings): the
        // same corrupted-settings-row null TrustedKeys element F1 guards against in Verify would
        // otherwise NRE inside the .Any/.Count/.RemoveAll fingerprint-equality predicates these
        // three actions use.
        var a = PgpTestKeys.Generate("a@x");
        var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { TrustedKeys = [Key(a), null!] });

        Assert.IsType<RedirectToActionResult>(
            await MakeController(repo, new LedgerStore(repo)).AddTrustedKey(b.ArmoredPublicKey));
        Assert.IsType<ViewResult>(
            await MakeController(repo, new LedgerStore(repo)).RemoveTrustedKeyConfirm(a.Fingerprint));
        Assert.IsType<RedirectToActionResult>(
            await MakeController(repo, new LedgerStore(repo)).RemoveTrustedKey(a.Fingerprint));

        var settings = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.DoesNotContain(settings!.TrustedKeys, k => k is not null && k.Fingerprint == a.Fingerprint);
        Assert.Contains(settings.TrustedKeys, k => k is not null && k.Fingerprint == b.Fingerprint);
    }

    // --- Finding I4: Remove must confirm first, and must actually remove by fingerprint. ---

    [Fact]
    public async Task RemoveTrustedKeyConfirm_redirects_with_an_error_for_an_unknown_fingerprint()
    {
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings());
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.RemoveTrustedKeyConfirm("0000000000000000000000000000000000FFFF");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Settings), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
    }

    [Fact]
    public async Task RemoveTrustedKey_removes_the_matching_key_by_fingerprint()
    {
        var key = PgpTestKeys.Generate("remove-me@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { TrustedKeys = [Key(key)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.RemoveTrustedKey(key.Fingerprint);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(SecSwitchController.Settings), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var settings = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Empty(settings!.TrustedKeys);
    }

    // ================================================================================
    // Final whole-branch review, Finding I1 (Important): SecSwitch could be enabled - and left
    // running - with FEWER trusted keys than its own quorum threshold. Quorum 2 with 1 key makes
    // every advisory Unverified, which is excluded from both the bell notification AND the alert
    // banner: a green settings page, a populated key table, and zero protection, indefinitely.
    // TrustStore.TryApplyRotation already enforced the right invariant
    // (working.Count < Math.Max(1, quorumThreshold)) - it was simply absent from the live admin path.
    // ================================================================================

    static SecSwitchSettings EnabledWith(int quorum, params PgpTestKey[] keys) => new()
    {
        Enabled = true,
        QuorumThreshold = quorum,
        TrustedKeys = keys.Select(Key).ToList()
    };

    [Fact]
    public async Task Settings_refuses_to_enable_with_fewer_trusted_keys_than_the_quorum()
    {
        var only = PgpTestKeys.Generate("only@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { TrustedKeys = [Key(only)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        // One trusted key, quorum 2: not empty, so the old Count == 0 guard let this straight through.
        var result = await controller.Settings(new SecSwitchSettings { Enabled = true, QuorumThreshold = 2 });

        Assert.IsType<ViewResult>(result); // redisplayed with errors, not redirected after a save
        Assert.False(controller.ModelState.IsValid);
        Assert.True(controller.ModelState.ContainsKey(nameof(SecSwitchSettings.TrustedKeys)));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.False(persisted!.Enabled); // nothing was saved
    }

    [Fact]
    public async Task Settings_refuses_to_raise_the_quorum_above_the_trusted_key_count()
    {
        // The same hole from the other side: an already-enabled, correctly-configured instance can be
        // pushed below quorum by raising the threshold rather than by removing a key.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(EnabledWith(2, a, b));
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Settings(new SecSwitchSettings { Enabled = true, QuorumThreshold = 5 });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(SecSwitchSettings.TrustedKeys)));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Equal(2, persisted!.QuorumThreshold); // unchanged
    }

    [Fact]
    public async Task Settings_saves_when_the_key_count_meets_the_quorum()
    {
        // The control: the guard must not block a legitimate, correctly-configured enable.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { TrustedKeys = [Key(a), Key(b)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Settings(new SecSwitchSettings
        { Enabled = true, QuorumThreshold = 2, FeedUrl = "https://feed.example/" });

        Assert.IsType<RedirectToActionResult>(result);
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.True(persisted!.Enabled);
        Assert.Equal(2, persisted.TrustedKeys.Count); // restored, not erased, by the save
    }

    [Fact]
    public async Task RemoveTrustedKey_refuses_to_drop_an_enabled_store_below_its_quorum()
    {
        // The endpoint had no floor guard at all - keys could be removed one at a time until a
        // still-enabled SecSwitch could no longer verify anything.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(EnabledWith(2, a, b));
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.RemoveTrustedKey(a.Fingerprint);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Equal(2, persisted!.TrustedKeys.Count); // nothing removed
        Assert.Contains(persisted.TrustedKeys, k => k.Fingerprint == a.Fingerprint);
    }

    [Fact]
    public async Task RemoveTrustedKey_still_allows_removal_above_the_quorum_floor()
    {
        // The control: three keys with a quorum of 2 leaves two after a removal, which is fine. A
        // guard that refused every removal outright would be its own usability trap.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var c = PgpTestKeys.Generate("c@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(EnabledWith(2, a, b, c));
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.RemoveTrustedKey(c.Fingerprint);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Equal(2, persisted!.TrustedKeys.Count);
        Assert.DoesNotContain(persisted.TrustedKeys, k => k.Fingerprint == c.Fingerprint);
    }

    [Fact]
    public async Task RemoveTrustedKey_still_allows_clearing_the_store_while_SecSwitch_is_disabled()
    {
        // The floor is scoped to Enabled deliberately: while SecSwitch is off nothing is being
        // protected, and an admin must still be able to remove a mistakenly-added key - including the
        // very first one, which an unconditional floor would make permanently unremovable. Re-enabling
        // is what the Settings guard above then blocks until the store is back at quorum, so there is
        // no window in which an ENABLED SecSwitch sits below its own quorum.
        var only = PgpTestKeys.Generate("only@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings { Enabled = false, QuorumThreshold = 2, TrustedKeys = [Key(only)] });
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.RemoveTrustedKey(only.Fingerprint);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Empty(persisted!.TrustedKeys);
    }

    // ================================================================================
    // Final whole-branch review, Finding I2 (Important): FeedUrl was unvalidated, and AdvisoryFetcher
    // refuses a non-https or unparseable URL silently by design (fails closed to "nothing fetched",
    // never throws, has no logger). Typing "http://..." saved cleanly and left SecSwitch permanently,
    // invisibly inert - no error, no log, no notification, ever.
    // ================================================================================

    [Theory]
    [InlineData("http://kukks.github.io/secswitch-advisories/")] // the exact trap: GitHub Pages redirects http->https
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://feed.example/")]
    [InlineData("/relative/path")]
    public async Task Settings_rejects_a_feed_url_AdvisoryFetcher_would_never_poll(string feedUrl)
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(EnabledWith(2, a, b));
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Settings(new SecSwitchSettings
        { Enabled = true, QuorumThreshold = 2, FeedUrl = feedUrl });

        Assert.IsType<ViewResult>(result);
        Assert.True(controller.ModelState.ContainsKey(nameof(SecSwitchSettings.FeedUrl)));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.NotEqual(feedUrl, persisted!.FeedUrl); // never saved
        // The validation and the fetcher must agree - a URL the page accepts that the fetcher then
        // refuses is the same silent-inertness bug wearing a different hat.
        Assert.False(AdvisoryFetcher.IsSupportedFeedUrl(feedUrl));
    }

    [Fact]
    public async Task Settings_accepts_an_https_feed_url()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(EnabledWith(2, a, b));
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.Settings(new SecSwitchSettings
        { Enabled = true, QuorumThreshold = 2, FeedUrl = "https://feed.example/advisories/" });

        Assert.IsType<RedirectToActionResult>(result);
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Equal("https://feed.example/advisories/", persisted!.FeedUrl);
    }

    [Fact]
    public void The_default_feed_url_is_one_the_fetcher_will_actually_poll()
    {
        // A shipped default that fails its own validation would make a fresh install unsaveable.
        Assert.True(AdvisoryFetcher.IsSupportedFeedUrl(new SecSwitchSettings().FeedUrl));
    }

    // ================================================================================
    // Final whole-branch review, Finding I3 (Important): AddTrustedKey screened neither revocation
    // nor expiry, while TrustStore.TryApplyRotation screened both - the stronger checks lived only on
    // the code path that is never executed today. AdvisoryVerifier.Verify deliberately checks neither
    // (out of scope for this wave, and noted as a residual), so this admission point is the only
    // place a revoked or expired signer can be kept out of the trust store at all.
    // ================================================================================

    [Fact]
    public async Task AddTrustedKey_refuses_a_revoked_key()
    {
        var revoked = PgpTestKeys.GenerateRevoked("revoked@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings());
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey(revoked.ArmoredPublicKey);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        Assert.False(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Empty(persisted!.TrustedKeys);
    }

    [Fact]
    public async Task AddTrustedKey_refuses_an_expired_key()
    {
        var expired = PgpTestKeys.GenerateExpired("expired@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings());
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey(expired.ArmoredPublicKey);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        Assert.False(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Empty(persisted!.TrustedKeys);
    }

    [Fact]
    public async Task AddTrustedKey_still_accepts_an_ordinary_unrevoked_unexpired_key()
    {
        // The control for the two refusals above: the screen must not reject healthy key material.
        var healthy = PgpTestKeys.Generate("healthy@x");
        var repo = new FakeSettingsRepository();
        await repo.UpdateSetting(new SecSwitchSettings());
        var controller = MakeController(repo, new LedgerStore(repo));

        var result = await controller.AddTrustedKey(healthy.ArmoredPublicKey);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var persisted = await repo.GetSettingAsync<SecSwitchSettings>();
        Assert.Equal(healthy.Fingerprint, Assert.Single(persisted!.TrustedKeys).Fingerprint);
    }
}
