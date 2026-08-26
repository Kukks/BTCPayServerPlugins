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
}
