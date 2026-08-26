using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class TrustStoreTests
{
    static TrustedKey Key(PgpTestKey k, string identity) =>
        new() { Fingerprint = k.Fingerprint, ArmoredPublicKey = k.ArmoredPublicKey, Identity = identity };

    static byte[] RotationJson(string[]? add = null, string[]? remove = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            add = add ?? [],
            remove = remove ?? []
        }));

    [Fact]
    public void Rotation_signed_by_current_quorum_adds_a_key()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var newcomer = PgpTestKeys.Generate("c@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add: [newcomer.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.True(ok, err);
        Assert.Equal(3, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == newcomer.Fingerprint);
    }

    [Fact]
    public void Rotation_signed_by_current_quorum_removes_a_key()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var compromised = PgpTestKeys.Generate("bad@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b"), Key(compromised, "bad") };

        var json = RotationJson(remove: [compromised.Fingerprint]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.True(ok, err);
        Assert.Equal(2, updated.Count);
        Assert.DoesNotContain(updated, k => k.Fingerprint == compromised.Fingerprint);
    }

    [Fact]
    public void Rotation_without_quorum_is_rejected()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var newcomer = PgpTestKeys.Generate("c@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add: [newcomer.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(current, json, [a.SignDetached(json)], 2, out _, out var err);

        Assert.False(ok);
        Assert.Contains("quorum", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_cannot_authorise_its_own_addition()
    {
        // The incoming key is not yet trusted, so its signature must not count.
        var a = PgpTestKeys.Generate("a@x");
        var newcomer = PgpTestKeys.Generate("c@x");
        var current = new List<TrustedKey> { Key(a, "a") };

        var json = RotationJson(add: [newcomer.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), newcomer.SignDetached(json)], 2, out _, out var err);

        Assert.False(ok);
        Assert.Contains("quorum", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rotation_cannot_empty_the_trust_store()
    {
        // Removing every key would permanently break all future rotations.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(remove: [a.Fingerprint, b.Fingerprint]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out _, out var err);

        Assert.False(ok);
        Assert.Contains("empty", err, StringComparison.OrdinalIgnoreCase);
    }

    // --- Additional coverage beyond the brief's given tests: the explicit security properties
    // (never throw on null; never partially apply a rotation) and the revocation/expiry admission
    // check deferred to this task from Task 4's review.

    [Fact]
    public void Null_current_trust_list_does_not_throw_and_fails_closed()
    {
        var a = PgpTestKeys.Generate("a@x");
        var json = RotationJson();
        var ok = TrustStore.TryApplyRotation(null!, json, [a.SignDetached(json)], 1, out var updated, out var err);

        Assert.False(ok);
        Assert.NotNull(updated);
        Assert.Empty(updated);
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Null_rotation_json_does_not_throw_and_fails_closed()
    {
        var a = PgpTestKeys.Generate("a@x");
        var current = new List<TrustedKey> { Key(a, "a") };

        var ok = TrustStore.TryApplyRotation(current, null!, [], 1, out var updated, out var err);

        Assert.False(ok);
        Assert.Single(updated);
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Null_signature_list_does_not_throw_and_fails_closed()
    {
        var a = PgpTestKeys.Generate("a@x");
        var current = new List<TrustedKey> { Key(a, "a") };
        var json = RotationJson();

        var ok = TrustStore.TryApplyRotation(current, json, null!, 1, out var updated, out var err);

        Assert.False(ok);
        Assert.Single(updated);
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Malformed_rotation_json_fails_closed_without_throwing()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };
        var garbage = Encoding.UTF8.GetBytes("not json at all { [ ");

        var ok = TrustStore.TryApplyRotation(
            current, garbage, [a.SignDetached(garbage), b.SignDetached(garbage)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.NotEmpty(err);
        Assert.Equal(2, updated.Count); // unchanged - never partially applied
    }

    [Fact]
    public void Unreadable_public_key_in_add_fails_closed_without_throwing()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add:
            ["-----BEGIN PGP PUBLIC KEY BLOCK-----\ngarbage\n-----END PGP PUBLIC KEY BLOCK-----"]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.NotEmpty(err);
        Assert.Equal(2, updated.Count); // unchanged
    }

    [Fact]
    public void Null_element_in_add_list_fails_closed_without_throwing()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add: [null!]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.Equal(2, updated.Count); // unchanged
    }

    [Fact]
    public void A_valid_remove_is_not_partially_applied_when_a_later_add_fails()
    {
        // Security property: fail closed with an error - never a partially-applied rotation. A
        // rotation combining a well-formed remove with an unreadable add must leave the trust store
        // completely untouched, not remove the key and then merely fail to add the new one.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(remove: [a.Fingerprint], add: ["not a real key"]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.Equal(2, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == a.Fingerprint); // NOT removed
    }

    [Fact]
    public void Revoked_key_is_refused_admission_during_rotation()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var revoked = PgpTestKeys.GenerateRevoked("revoked@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add: [revoked.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.Contains("revoked", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, updated.Count);
        Assert.DoesNotContain(updated, k => k.Fingerprint == revoked.Fingerprint);
    }

    [Fact]
    public void Expired_key_is_refused_admission_during_rotation()
    {
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var expired = PgpTestKeys.GenerateExpired("expired@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add: [expired.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.Contains("expired", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, updated.Count);
        Assert.DoesNotContain(updated, k => k.Fingerprint == expired.Fingerprint);
    }

    [Fact]
    public void Already_trusted_revoked_key_is_not_retroactively_scanned_on_reapplication()
    {
        // Out of scope by explicit design: TryApplyRotation only screens a key being newly admitted
        // right now. A key that is already in `current` - even if it has since been revoked - is
        // not retroactively re-checked just because a rotation happens to list its armored key
        // again in `add` (a harmless, idempotent no-op for an already-trusted fingerprint).
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var revoked = PgpTestKeys.GenerateRevoked("revoked@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b"), Key(revoked, "revoked") };

        var json = RotationJson(add: [revoked.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.True(ok, err);
        Assert.Equal(3, updated.Count);
        Assert.Contains(updated, k => k.Fingerprint == revoked.Fingerprint);
    }
}
