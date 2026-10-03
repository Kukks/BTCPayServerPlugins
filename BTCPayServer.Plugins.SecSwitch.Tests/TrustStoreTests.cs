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
    public void A_key_cannot_authorise_its_own_addition_even_when_enough_other_trusted_keys_exist()
    {
        // Stronger than the sibling test above (review-requested): here `current` holds TWO trusted
        // keys (a and b), so a naive "count any 2 signatures" implementation would wrongly reach
        // quorum on [a, newcomer] alone - b never signs at all. Isolates "newcomer's signature is
        // present but does not count" from "there simply are not enough signers", which the sibling
        // test alone (only one trusted key in `current`) cannot fully distinguish.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var newcomer = PgpTestKeys.Generate("c@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(add: [newcomer.ArmoredPublicKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), newcomer.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.Contains("quorum", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, updated.Count); // unchanged
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
    public void Null_rotation_json_does_not_throw_and_fails_closed_via_quorum_check()
    {
        // Review precision nit: a null payload can never meet quorum (AdvisoryVerifier.Verify's own
        // null-payload guard is unconditional, before JSON is ever touched), so the quorum check -
        // not JSON deserialization - is structurally the only code path a null rotationJson can ever
        // reach. Asserting that explicitly (via the error text) rather than leaving a test name that
        // implied JSON-path coverage it could never actually exercise.
        var a = PgpTestKeys.Generate("a@x");
        var current = new List<TrustedKey> { Key(a, "a") };

        var ok = TrustStore.TryApplyRotation(current, null!, [], 1, out var updated, out var err);

        Assert.False(ok);
        Assert.Single(updated);
        Assert.Contains("quorum", err, StringComparison.OrdinalIgnoreCase);
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

    // --- Fix-round additions (post-review): Finding 1 (consistency with AdvisoryVerifier's own
    // admission rules), Finding 2 (end-to-end round-trip - the CRITICAL cross-task contract was
    // previously only asserted by construction/comment), Minor 4 (add/remove collision), and
    // Minor 5 (below-quorum-threshold, not just empty).

    [Fact]
    public void Rotation_result_actually_functions_as_a_trust_store_for_a_later_rotation()
    {
        // Finding 2: the CRITICAL cross-task contract - a fingerprint TrustStore stores must be one
        // AdvisoryVerifier.Verify can actually use for quorum, not merely one that looks correct -
        // had no end-to-end test. Proves it by feeding a first rotation's result back in as `current`
        // for a SECOND rotation that the newly-added key itself co-signs: this can only succeed if
        // the first rotation's output is genuinely usable by AdvisoryVerifier, not just correctly
        // labelled. This is also the regression guard that would have caught Finding 1 directly (an
        // admitted-but-unusable key would make this second step fail to reach quorum).
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var newcomer = PgpTestKeys.Generate("c@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var firstJson = RotationJson(add: [newcomer.ArmoredPublicKey]);
        var firstOk = TrustStore.TryApplyRotation(
            current, firstJson, [a.SignDetached(firstJson), b.SignDetached(firstJson)], 2,
            out var afterFirst, out var firstErr);
        Assert.True(firstOk, firstErr);

        var another = PgpTestKeys.Generate("d@x");
        var secondJson = RotationJson(add: [another.ArmoredPublicKey]);
        var secondOk = TrustStore.TryApplyRotation(
            afterFirst, secondJson, [a.SignDetached(secondJson), newcomer.SignDetached(secondJson)], 2,
            out var afterSecond, out var secondErr);

        Assert.True(secondOk, secondErr);
        Assert.Equal(4, afterSecond.Count);
        Assert.Contains(afterSecond, k => k.Fingerprint == another.Fingerprint);
    }

    [Fact]
    public void Rotation_refuses_to_add_a_key_that_AdvisoryVerifier_would_later_refuse_to_load()
    {
        // Finding 1: AdvisoryVerifier.LoadTrustedKeys skips any trusted-key blob over its own
        // MaxTrustedKeyBlobLength (256 KiB) byte cap - but AdvisoryVerifier.FingerprintOf has no such
        // cap. Before the fix, TrustStore would admit such a blob under a *correct* fingerprint,
        // counting it toward working.Count, even though AdvisoryVerifier could never load it again -
        // a silent, permanent brick. Pads a genuine key's armored blob past the cap the same way
        // AdvisoryVerifierTests pads an oversized signature block: filler appended after a complete,
        // genuine armor block is inert to parsing (armor decoding stops at the END marker) but still
        // counts toward the raw byte-length cap.
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var oversized = PgpTestKeys.Generate("bloated@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var bloatedArmoredKey = oversized.ArmoredPublicKey + new string('X', 256 * 1024);

        var json = RotationJson(add: [bloatedArmoredKey]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        // Final whole-branch review, deferred-9: this asserted only Assert.NotEmpty(err), which every
        // other failure branch in TryApplyRotation also satisfies - the test would have stayed green
        // if the rotation had been refused for a completely different reason (unreadable key,
        // add/remove conflict, below-quorum result), so it did not actually pin the regression it
        // exists for. Assert on the specific refusal instead.
        Assert.Contains("could never load as", err);
        Assert.Contains(oversized.Fingerprint, err);
        Assert.Equal(2, updated.Count); // unchanged
        Assert.DoesNotContain(updated, k => k.Fingerprint == oversized.Fingerprint);
    }

    [Fact]
    public void Rotation_refuses_when_the_same_key_is_both_added_and_removed()
    {
        // Minor 4: `remove` shows a human-readable fingerprint but `add` shows an opaque armored
        // blob - a reviewing signer trusting what `remove` visibly says could co-sign what reads as
        // "revoke K" while Remove-then-Add ordering would actually leave K trusted (removed, then
        // immediately re-admitted). Must be refused outright, not resolved by silently picking
        // whichever of add/remove "wins".
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var k = PgpTestKeys.Generate("k@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b"), Key(k, "k") };

        var json = RotationJson(add: [k.ArmoredPublicKey], remove: [k.Fingerprint]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.NotEmpty(err);
        Assert.Equal(3, updated.Count);
        Assert.Contains(updated, key => key.Fingerprint == k.Fingerprint); // untouched either way
    }

    [Fact]
    public void Rotation_refuses_when_result_would_fall_below_the_quorum_threshold()
    {
        // Minor 5: non-empty is not the property that actually matters. Threshold 2, current {a, b}:
        // removing just `a` leaves {b} - one key, not empty, but a store of one can never again meet
        // a quorum of 2. Before the fix this rotation would have succeeded (working.Count == 1 != 0).
        var a = PgpTestKeys.Generate("a@x"); var b = PgpTestKeys.Generate("b@x");
        var current = new List<TrustedKey> { Key(a, "a"), Key(b, "b") };

        var json = RotationJson(remove: [a.Fingerprint]);
        var ok = TrustStore.TryApplyRotation(
            current, json, [a.SignDetached(json), b.SignDetached(json)], 2, out var updated, out var err);

        Assert.False(ok);
        Assert.NotEmpty(err);
        Assert.Equal(2, updated.Count); // unchanged - the original 2-key store, not the 1-key result
        Assert.Contains(updated, key => key.Fingerprint == a.Fingerprint);
    }
}
