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
/// malformed or absent resource. "Already bootstrapped" latching is exercised at the LedgerStore level
/// (see LedgerStoreTests) and the periodic-task orchestration level, not here - this file exercises
/// only Apply's own per-call contract: given a resource byte payload and a current key list, what does
/// the resulting key list look like.
/// </summary>
public class TrustRootBootstrapperTests
{
    static byte[] TrustRootJson(params string[] keys) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { comment = "test", keys }));

    static TrustedKey Existing(PgpTestKey k) =>
        new() { Fingerprint = k.Fingerprint, ArmoredPublicKey = k.ArmoredPublicKey, Identity = k.Fingerprint };

    [Fact]
    public void Bundled_key_is_added_with_a_freshly_derived_fingerprint()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var updated = TrustRootBootstrapper.Apply(
            [], TrustRootJson(bundled.ArmoredPublicKey), NullLogger.Instance);

        var key = Assert.Single(updated);
        Assert.Equal(bundled.Fingerprint, key.Fingerprint);
        Assert.Equal(bundled.ArmoredPublicKey, key.ArmoredPublicKey);
        // Never trusted from anywhere else in the file (there is nowhere else to trust it from - the
        // schema carries only armored keys) - Identity defaults to the derived fingerprint, matching
        // TrustStore.TryApplyRotation's and SecSwitchController.AddTrustedKey's own convention for a
        // key admitted without any other identity information.
        Assert.Equal(bundled.Fingerprint, key.Identity);
    }

    [Fact]
    public void Already_present_bundled_key_is_not_duplicated()
    {
        var bundled = PgpTestKeys.Generate("founder@x");
        var current = new List<TrustedKey> { Existing(bundled) };

        var updated = TrustRootBootstrapper.Apply(
            current, TrustRootJson(bundled.ArmoredPublicKey), NullLogger.Instance);

        Assert.Single(updated);
    }

    [Fact]
    public void Admin_added_keys_are_preserved_alongside_a_newly_bundled_key()
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var bundled = PgpTestKeys.Generate("founder@x");
        var current = new List<TrustedKey> { Existing(adminKey) };

        var updated = TrustRootBootstrapper.Apply(
            current, TrustRootJson(bundled.ArmoredPublicKey), NullLogger.Instance);

        Assert.Equal(2, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == adminKey.Fingerprint);
        Assert.Contains(updated, k => k.Fingerprint == bundled.Fingerprint);
    }

    [Fact]
    public void Duplicate_entries_within_the_same_resource_are_added_only_once()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var updated = TrustRootBootstrapper.Apply(
            [], TrustRootJson(bundled.ArmoredPublicKey, bundled.ArmoredPublicKey), NullLogger.Instance);

        Assert.Single(updated);
    }

    [Fact]
    public void Unreadable_bundled_key_is_skipped_without_aborting_the_others()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var updated = TrustRootBootstrapper.Apply(
            [], TrustRootJson("not a real armored key", bundled.ArmoredPublicKey), NullLogger.Instance);

        var key = Assert.Single(updated);
        Assert.Equal(bundled.Fingerprint, key.Fingerprint);
    }

    [Fact]
    public void Blank_entry_in_the_keys_array_is_skipped()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var updated = TrustRootBootstrapper.Apply(
            [], TrustRootJson("", "   ", bundled.ArmoredPublicKey), NullLogger.Instance);

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

        var updated = TrustRootBootstrapper.Apply(current, TrustRootJson(), NullLogger.Instance);

        Assert.Single(updated);
        Assert.Equal(adminKey.Fingerprint, updated[0].Fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void Absent_or_empty_resource_bytes_fail_closed_without_throwing(byte[]? bytes)
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var current = new List<TrustedKey> { Existing(adminKey) };

        var updated = TrustRootBootstrapper.Apply(current, bytes, NullLogger.Instance);

        Assert.Single(updated); // unchanged, not wiped
    }

    [Fact]
    public void Malformed_json_fails_closed_without_throwing()
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var current = new List<TrustedKey> { Existing(adminKey) };
        var malformed = Encoding.UTF8.GetBytes("{ not json");

        var updated = TrustRootBootstrapper.Apply(current, malformed, NullLogger.Instance);

        Assert.Single(updated);
        Assert.Equal(adminKey.Fingerprint, updated[0].Fingerprint);
    }

    [Fact]
    public void Json_of_a_different_shape_fails_closed_without_throwing()
    {
        var current = new List<TrustedKey>();
        var wrongShape = Encoding.UTF8.GetBytes("[1,2,3]");

        var updated = TrustRootBootstrapper.Apply(current, wrongShape, NullLogger.Instance);

        Assert.Empty(updated);
    }

    [Fact]
    public void Null_current_list_is_treated_as_empty()
    {
        var bundled = PgpTestKeys.Generate("founder@x");

        var updated = TrustRootBootstrapper.Apply(
            null!, TrustRootJson(bundled.ArmoredPublicKey), NullLogger.Instance);

        Assert.Single(updated);
    }

    [Fact]
    public void Null_element_in_current_list_does_not_abort_processing()
    {
        var adminKey = PgpTestKeys.Generate("admin@x");
        var bundled = PgpTestKeys.Generate("founder@x");
        var current = new List<TrustedKey> { Existing(adminKey), null! };

        var updated = TrustRootBootstrapper.Apply(
            current, TrustRootJson(bundled.ArmoredPublicKey), NullLogger.Instance);

        Assert.Equal(2, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == adminKey.Fingerprint);
        Assert.Contains(updated, k => k.Fingerprint == bundled.Fingerprint);
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
        var updated = TrustRootBootstrapper.Apply([], bytes, NullLogger.Instance);
        Assert.Empty(updated);
    }
}
