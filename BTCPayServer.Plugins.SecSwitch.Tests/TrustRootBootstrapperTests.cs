using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// <summary>
/// Hard requirement 3 (Task 13): bootstrap the trust store from the embedded trust-root resource on
/// first run, deriving every fingerprint from the key material itself (never trusting a written one),
/// adding a bundled key only if it is not already present, and failing closed - never throwing - on a
/// malformed or absent resource. "Already offered" latching (Task 13 review, Finding I1 fix - replaced
/// a single permanent bool with a per-fingerprint set specifically so a later plugin release that
/// finally populates the bundle is not permanently locked out of installing those keys on an
/// already-upgraded instance) is exercised both here, at the Apply level (a fresh, empty
/// `alreadyOffered` vs. one that already lists a fingerprint), and at the LedgerStore level (see
/// LedgerStoreTests) and the periodic-task orchestration level - this file exercises Apply's own
/// per-call contract: given a resource byte payload, a current key list, and a set of previously
/// offered fingerprints, what does the resulting key list and newly-offered set look like.
/// </summary>
public class TrustRootBootstrapperTests
{
    static byte[] TrustRootJson(params string[] keys) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { comment = "test", keys }));

    static TrustedKey Existing(PgpTestKey k) =>
        new() { Fingerprint = k.Fingerprint, ArmoredPublicKey = k.ArmoredPublicKey, Identity = k.Fingerprint };

    static readonly IReadOnlySet<string> NoneOffered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Bundled_key_is_added_with_a_freshly_derived_fingerprint()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            [], TrustRootJson(bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        var key = Assert.Single(updated);
        Assert.Equal(bundled.Fingerprint, key.Fingerprint);
        Assert.Equal(bundled.ArmoredPublicKey, key.ArmoredPublicKey);
        // Never trusted from anywhere else in the file (there is nowhere else to trust it from - the
        // schema carries only armored keys) - Identity defaults to the derived fingerprint, matching
        // TrustStore.TryApplyRotation's and SecSwitchController.AddTrustedKey's own convention for a
        // key admitted without any other identity information.
        Assert.Equal(bundled.Fingerprint, key.Identity);
        Assert.Equal([bundled.Fingerprint], newlyOffered);
    }

    [Fact]
    public void Already_present_bundled_key_is_not_duplicated()
    {
        var bundled = PgpTestKeys.Generate("founder@x");
        var current = new List<TrustedKey> { Existing(bundled) };

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            current, TrustRootJson(bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        Assert.Single(updated);
        // Not yet in the offered set, so it IS newly offered this call even though it was already
        // trusted (e.g. an admin pasted the same key by hand before the bootstrap ever ran) - the
        // caller still needs to record it as offered so a later removal of it is respected.
        Assert.Equal([bundled.Fingerprint], newlyOffered);
    }

    [Fact]
    public void Admin_added_keys_are_preserved_alongside_a_newly_bundled_key()
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var bundled = PgpTestKeys.Generate("founder@x");
        var current = new List<TrustedKey> { Existing(adminKey) };

        var (updated, _) = TrustRootBootstrapper.Apply(
            current, TrustRootJson(bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        Assert.Equal(2, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == adminKey.Fingerprint);
        Assert.Contains(updated, k => k.Fingerprint == bundled.Fingerprint);
    }

    [Fact]
    public void Duplicate_entries_within_the_same_resource_are_added_only_once()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            [], TrustRootJson(bundled.ArmoredPublicKey, bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        Assert.Single(updated);
        Assert.Single(newlyOffered); // deduped, not offered twice
    }

    [Fact]
    public void Unreadable_bundled_key_is_skipped_without_aborting_the_others()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var (updated, _) = TrustRootBootstrapper.Apply(
            [], TrustRootJson("not a real armored key", bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        var key = Assert.Single(updated);
        Assert.Equal(bundled.Fingerprint, key.Fingerprint);
    }

    [Fact]
    public void Blank_entry_in_the_keys_array_is_skipped()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var (updated, _) = TrustRootBootstrapper.Apply(
            [], TrustRootJson("", "   ", bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        var key = Assert.Single(updated);
        Assert.Equal(bundled.Fingerprint, key.Fingerprint);
    }

    [Fact]
    public void Empty_keys_array_leaves_the_current_store_unchanged()
    {
        // Today's real, shipped Resources/trust-root.json - "an empty list means SecSwitch can never
        // verify an advisory" per its own comment. Must not throw and must not fabricate anything.
        var adminKey = PgpTestKeys.Generate("admin@x");
        var current = new List<TrustedKey> { Existing(adminKey) };

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(current, TrustRootJson(), NoneOffered, NullLogger.Instance);

        Assert.Single(updated);
        Assert.Equal(adminKey.Fingerprint, updated[0].Fingerprint);
        Assert.Empty(newlyOffered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void Absent_or_empty_resource_bytes_fail_closed_without_throwing(byte[]? bytes)
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var current = new List<TrustedKey> { Existing(adminKey) };

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(current, bytes, NoneOffered, NullLogger.Instance);

        Assert.Single(updated); // unchanged, not wiped
        Assert.Empty(newlyOffered);
    }

    [Fact]
    public void Malformed_json_fails_closed_without_throwing()
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var current = new List<TrustedKey> { Existing(adminKey) };
        var malformed = Encoding.UTF8.GetBytes("{ not json");

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(current, malformed, NoneOffered, NullLogger.Instance);

        Assert.Single(updated);
        Assert.Equal(adminKey.Fingerprint, updated[0].Fingerprint);
        Assert.Empty(newlyOffered);
    }

    [Fact]
    public void Json_of_a_different_shape_fails_closed_without_throwing()
    {
        var current = new List<TrustedKey>();
        var wrongShape = Encoding.UTF8.GetBytes("[1,2,3]");

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(current, wrongShape, NoneOffered, NullLogger.Instance);

        Assert.Empty(updated);
        Assert.Empty(newlyOffered);
    }

    [Fact]
    public void Null_current_list_is_treated_as_empty()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var (updated, _) = TrustRootBootstrapper.Apply(
            null!, TrustRootJson(bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        Assert.Single(updated);
    }

    [Fact]
    public void Null_element_in_current_list_does_not_abort_processing()
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var bundled = PgpTestKeys.Generate("founder@x");
        var current = new List<TrustedKey> { Existing(adminKey), null! };

        var (updated, _) = TrustRootBootstrapper.Apply(
            current, TrustRootJson(bundled.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        Assert.Equal(2, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == adminKey.Fingerprint);
        Assert.Contains(updated, k => k.Fingerprint == bundled.Fingerprint);
    }

    [Fact]
    public void Null_already_offered_set_is_treated_as_empty()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            [], TrustRootJson(bundled.ArmoredPublicKey), null, NullLogger.Instance);

        Assert.Single(updated);
        Assert.Equal([bundled.Fingerprint], newlyOffered);
    }

    // --- Task 13 review, Finding I1 (Important) fix: the two scenarios the reviewer specifically
    // required, proving the offered-set design (not the rejected "only latch when > 0 added"
    // alternative) actually satisfies both halves of the requirement at once.

    [Fact]
    public void A_new_bundled_key_offered_for_the_first_time_is_added()
    {
        // Simulates a plugin upgrade that finally populates a previously-empty bundle: the earlier
        // run(s) offered nothing (alreadyOffered is empty, exactly like today's real shipped
        // resource), and this run's bundle now lists a real key for the first time.
        var newBundledKey = PgpTestKeys.Generate("founder@x");

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            [], TrustRootJson(newBundledKey.ArmoredPublicKey), NoneOffered, NullLogger.Instance);

        Assert.Contains(updated, k => k.Fingerprint == newBundledKey.Fingerprint);
        Assert.Equal([newBundledKey.Fingerprint], newlyOffered);
    }

    [Fact]
    public void A_previously_offered_then_removed_key_is_not_re_added()
    {
        // The exact scenario Finding I1 exists to prevent: a bundled key was offered by an earlier
        // run (whether or not it was ever actually admitted), an admin removed it afterwards, and the
        // SAME bundle is presented again on a later run (restart, or an unrelated plugin upgrade that
        // does not touch the bundle). The key must not silently reappear.
        var bundledKey = PgpTestKeys.Generate("founder@x");
        var alreadyOffered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bundledKey.Fingerprint };
        var currentAfterAdminRemoval = new List<TrustedKey>(); // admin removed it - not present now

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            currentAfterAdminRemoval, TrustRootJson(bundledKey.ArmoredPublicKey), alreadyOffered, NullLogger.Instance);

        Assert.Empty(updated); // not re-added
        Assert.Empty(newlyOffered); // and not re-recorded as newly offered either - nothing changed
    }

    [Fact]
    public void A_second_new_key_added_to_the_bundle_alongside_a_previously_offered_one_is_still_added()
    {
        // Guards against an implementation that (incorrectly) treats "any fingerprint already
        // offered" as a reason to skip the WHOLE resource - each fingerprint must be considered
        // independently.
        var alreadyOfferedKey = PgpTestKeys.Generate("old@x");
        var newKey = PgpTestKeys.Generate("new@x");
        var alreadyOffered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { alreadyOfferedKey.Fingerprint };
        var current = new List<TrustedKey> { Existing(alreadyOfferedKey) };

        var (updated, newlyOffered) = TrustRootBootstrapper.Apply(
            current, TrustRootJson(alreadyOfferedKey.ArmoredPublicKey, newKey.ArmoredPublicKey),
            alreadyOffered, NullLogger.Instance);

        Assert.Equal(2, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == newKey.Fingerprint);
        Assert.Equal([newKey.Fingerprint], newlyOffered);
    }

    // --- PR #151 review (CodeRabbit), Finding F2 (the most important of the batch): a fingerprint
    // was previously added to newlyOffered BEFORE the TryLoadTrustedKey admission check ran, so a
    // bundled key that derives a fingerprint but FAILS admission (e.g. one over AdvisoryVerifier's
    // own MaxTrustedKeyBlobLength byte cap) got latched into the caller's persisted
    // OfferedTrustRootFingerprints on its very first run - permanently, since "already offered" is
    // never reconsidered. A later plugin release shipping the SAME key in a loadable form could then
    // never install it: the trust store would stay empty and quorum could never be met, with the
    // plugin inert save for a warning log. Fixed by only recording a fingerprint as offered once it
    // is admitted or found already trusted - never on a failed admission attempt.

    [Fact]
    public void A_bundled_key_that_fails_admission_is_not_latched_and_is_added_once_it_becomes_loadable()
    {
        // Pads a genuine key's armored blob past AdvisoryVerifier's own 256 KiB
        // MaxTrustedKeyBlobLength cap - the same oversized-blob construction
        // TrustStoreTests.Rotation_refuses_to_add_a_key_that_AdvisoryVerifier_would_later_refuse_to_load
        // uses for the identical admission failure. FingerprintOf has no such cap (armor decoding
        // stops at the END marker, so the trailing filler is inert to parsing but still counts
        // toward TryLoadTrustedKey's raw byte-length check), so the fingerprint derives cleanly even
        // though the key can never actually be loaded as trusted.
        var bundled = PgpTestKeys.Generate("founder@x");
        var bloatedArmoredKey = bundled.ArmoredPublicKey + new string('X', 256 * 1024);

        var (firstRunUpdated, firstRunNewlyOffered) = TrustRootBootstrapper.Apply(
            [], TrustRootJson(bloatedArmoredKey), NoneOffered, NullLogger.Instance);

        Assert.Empty(firstRunUpdated); // not admitted
        Assert.Empty(firstRunNewlyOffered); // and, critically, NOT latched as offered either

        // Simulates the caller (SecSwitchPeriodicTask) persisting run 1's (empty) newlyOffered via
        // LedgerStore.RecordTrustRootOfferedAsync, then a LATER run - e.g. a follow-up plugin
        // release correcting the bundle - presenting the SAME fingerprint in a genuinely loadable
        // form.
        var (secondRunUpdated, secondRunNewlyOffered) = TrustRootBootstrapper.Apply(
            firstRunUpdated, TrustRootJson(bundled.ArmoredPublicKey),
            new HashSet<string>(firstRunNewlyOffered, StringComparer.OrdinalIgnoreCase), NullLogger.Instance);

        var key = Assert.Single(secondRunUpdated);
        Assert.Equal(bundled.Fingerprint, key.Fingerprint);
        Assert.Equal([bundled.Fingerprint], secondRunNewlyOffered);
    }

    // --- End-to-end: proves the real embedded Resources/trust-root.json (shipped in the plugin
    // assembly, see the csproj's <EmbeddedResource Include="Resources\**" />) is actually discoverable
    // and readable through ReadEmbeddedTrustRoot - a resource-naming mismatch (e.g. an incorrect
    // assumption about the default RootNamespace-derived manifest resource name) would only ever be
    // caught by a test that goes through real reflection against the real built assembly, never by
    // Apply's own byte-array-driven tests above.

    [Fact]
    public void Real_embedded_trust_root_resource_is_discoverable_and_parses_cleanly()
    {
        var bytes = TrustRootBootstrapper.ReadEmbeddedTrustRoot(NullLogger.Instance);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);

        // Today's shipped file's own "keys": [] - applying it against an empty store must yield an
        // empty store, not throw. If a future commit populates the bundle, this assertion (not the
        // resource lookup itself) is the one expected to need updating.
        var (updated, newlyOffered) = TrustRootBootstrapper.Apply([], bytes, NoneOffered, NullLogger.Instance);
        Assert.Empty(updated);
        Assert.Empty(newlyOffered);
    }
}
