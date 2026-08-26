using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryVerifierTests
{
    static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{"id":"a","severity":"high"}""");

    static Dictionary<string, string> Trust(params PgpTestKey[] keys)
        => keys.ToDictionary(k => k.Fingerprint, k => k.ArmoredPublicKey);

    [Fact]
    public void Two_valid_trusted_signatures_meet_a_quorum_of_two()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var b = PgpTestKeys.Generate("b@example.com");

        var result = AdvisoryVerifier.Verify(
            Payload, [a.SignDetached(Payload), b.SignDetached(Payload)], Trust(a, b), quorumThreshold: 2);

        Assert.True(result.QuorumMet);
        Assert.Equal(2, result.TrustedValidCount);
        Assert.All(result.Signatures, s => Assert.Equal(SignatureStatus.ValidTrusted, s.Status));
    }

    [Fact]
    public void One_signature_does_not_meet_a_quorum_of_two()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(
            Payload, [a.SignDetached(Payload)], Trust(a), quorumThreshold: 2);

        Assert.False(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        Assert.Equal(SignatureStatus.ValidTrusted, Assert.Single(result.Signatures).Status);
    }

    [Fact]
    public void Tampered_payload_invalidates_every_signature()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var b = PgpTestKeys.Generate("b@example.com");
        var sigs = new[] { a.SignDetached(Payload), b.SignDetached(Payload) };

        var tampered = Encoding.UTF8.GetBytes("""{"id":"a","severity":"low"}""");
        var result = AdvisoryVerifier.Verify(tampered, sigs, Trust(a, b), quorumThreshold: 2);

        Assert.False(result.QuorumMet);
        Assert.Equal(0, result.TrustedValidCount);
        // Both signatures' claimed issuer key ids honestly name a trusted signer (nothing here
        // tampers with the signature packets themselves - only the payload changed after signing),
        // so brute-force verification against every trusted key fails for both, and the informative
        // label is "found the claimed trusted key, but Verify() rejected it" - i.e. InvalidSignature
        // - not UnknownSigner. Neither status counts toward quorum either way.
        Assert.All(result.Signatures, s => Assert.Equal(SignatureStatus.InvalidSignature, s.Status));
        // The reported fingerprint is a *claim*, not a verified fact - it must use the "claimed:"
        // form so a future admin UI can never mistake it for the bare 40-hex ValidTrusted shape.
        Assert.All(result.Signatures, s => Assert.StartsWith("claimed:", s.Fingerprint));
        Assert.Contains(result.Signatures, s => s.Fingerprint == $"claimed:{a.Fingerprint}");
        Assert.Contains(result.Signatures, s => s.Fingerprint == $"claimed:{b.Fingerprint}");
    }

    [Fact]
    public void Untrusted_signer_does_not_count_toward_quorum()
    {
        var trusted = PgpTestKeys.Generate("a@example.com");
        var stranger = PgpTestKeys.Generate("evil@example.com");

        var result = AdvisoryVerifier.Verify(
            Payload,
            [trusted.SignDetached(Payload), stranger.SignDetached(Payload)],
            Trust(trusted),                       // stranger deliberately absent
            quorumThreshold: 2);

        Assert.False(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        Assert.Contains(result.Signatures, s => s.Status == SignatureStatus.ValidTrusted);
        // The stranger's key is not merely unverified against a trusted key it claims to be - it
        // isn't claiming to be any trusted key at all, so it must read as UnknownSigner, not
        // InvalidSignature (which would wrongly suggest a trusted signer's signature was tampered).
        Assert.Contains(result.Signatures, s => s.Status == SignatureStatus.UnknownSigner);
    }

    [Fact]
    public void Duplicate_signatures_from_one_key_count_only_once()
    {
        // Otherwise a single compromised key could forge a quorum by signing twice.
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(
            Payload, [a.SignDetached(Payload), a.SignDetached(Payload)], Trust(a), quorumThreshold: 2);

        Assert.False(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        // The dedup happens at the quorum-counting layer (by fingerprint), not by suppressing the
        // second signature's own result - both are genuinely, individually valid.
        Assert.All(result.Signatures, s => Assert.Equal(SignatureStatus.ValidTrusted, s.Status));
    }

    [Fact]
    public void Malformed_signature_is_reported_invalid_and_does_not_throw()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(
            Payload, ["-----BEGIN PGP SIGNATURE-----\ngarbage\n-----END PGP SIGNATURE-----"],
            Trust(a), quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        var sig = Assert.Single(result.Signatures);
        Assert.Equal(SignatureStatus.Malformed, sig.Status);
        Assert.Equal("", sig.Fingerprint);
    }

    [Fact]
    public void Empty_signature_list_never_meets_quorum()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        Assert.False(AdvisoryVerifier.Verify(Payload, [], Trust(a), quorumThreshold: 1).QuorumMet);
    }

    [Fact]
    public void Quorum_threshold_below_one_is_clamped_to_one()
    {
        // A misconfigured threshold of 0 must never mean "no signatures required".
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(Payload, [], Trust(a), quorumThreshold: 0);
        Assert.False(result.QuorumMet);
        Assert.Equal(1, result.Required);
    }

    // --- Not in the original brief: Verify's parameters are typed non-nullable, but nullable
    // reference types are only a compile-time hint, not a runtime guarantee - a caller across an
    // assembly boundary can still pass null. These cover security property 6 ("Verify must never
    // throw for any input") for the three reference-typed parameters, using null! to simulate a
    // caller that violates the annotation.

    [Fact]
    public void Null_payload_does_not_throw_and_fails_closed()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(null!, [a.SignDetached(Payload)], Trust(a), quorumThreshold: 1);
        Assert.False(result.QuorumMet);
        Assert.Equal(0, result.TrustedValidCount);
    }

    [Fact]
    public void Null_signature_list_does_not_throw_and_fails_closed()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(Payload, null!, Trust(a), quorumThreshold: 1);
        Assert.False(result.QuorumMet);
        Assert.Equal(0, result.TrustedValidCount);
    }

    [Fact]
    public void Null_trusted_keys_does_not_throw_and_fails_closed()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(Payload, [a.SignDetached(Payload)], null!, quorumThreshold: 1);
        Assert.False(result.QuorumMet);
        Assert.Equal(0, result.TrustedValidCount);
    }

    // --- Fix-round additions: the try-all-trusted-keys rework (AdvisoryVerifier.VerifyOne) and the
    // multi-signature parsing fix (AdvisoryVerifier.ParseSignatures). The original suite above only
    // ever exercised a bare single-key ring and one signature per armored block - a key shape and
    // block shape that do not occur in production.

    [Fact]
    public void Subkey_signed_advisory_reaches_quorum_attributed_to_primary_fingerprint()
    {
        // The hardened, realistic OpenPGP layout: an offline certify-only primary plus a signing
        // subkey - what gpg --detach-sign picks automatically. Before the rework, ReadPublicKey /
        // LoadTrustedKeys only ever indexed the master key by KeyId, so a subkey-signed advisory
        // could never be found: it would come back Valid but Untrusted forever, and a genuine
        // advisory from the intended signers would never reach quorum.
        var withSubkey = PgpTestKeys.GenerateWithSigningSubkey("subkey-owner@example.com");

        var result = AdvisoryVerifier.Verify(
            Payload, [withSubkey.SignDetached(Payload)], Trust(withSubkey), quorumThreshold: 1);

        Assert.True(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        var sig = Assert.Single(result.Signatures);
        Assert.Equal(SignatureStatus.ValidTrusted, sig.Status);
        // Attributed to the ring's primary/master fingerprint, not a separate subkey identity - so
        // quorum dedup still works per-signer regardless of which of their keys actually signed.
        Assert.Equal(withSubkey.Fingerprint, sig.Fingerprint);
    }

    [Fact]
    public void Multiple_trusted_signatures_in_one_armored_block_are_all_found()
    {
        // A maintainer who signs twice into the same .asc file (the natural result of appending
        // gpg --detach-sign output) produces one armor block containing a PgpSignatureList with
        // Count > 1. Before the fix, only list[0] was ever examined, so every signature after the
        // first silently vanished and quorum could never form from a single combined file.
        var a = PgpTestKeys.Generate("a@example.com");
        var b = PgpTestKeys.Generate("b@example.com");
        var combined = PgpTestKeys.CombineArmoredSignatures(a.SignDetached(Payload), b.SignDetached(Payload));

        var result = AdvisoryVerifier.Verify(Payload, [combined], Trust(a, b), quorumThreshold: 2);

        Assert.True(result.QuorumMet);
        Assert.Equal(2, result.TrustedValidCount);
        Assert.Equal(2, result.Signatures.Count);
        Assert.All(result.Signatures, s => Assert.Equal(SignatureStatus.ValidTrusted, s.Status));
    }

    [Fact]
    public void Untrusted_signature_preceding_a_trusted_one_in_one_block_does_not_hide_it()
    {
        // Prepending one well-formed junk/untrusted signature ahead of a genuine trusted one used to
        // erase the genuine one, since only list[0] (the junk entry) was ever examined.
        var trusted = PgpTestKeys.Generate("a@example.com");
        var stranger = PgpTestKeys.Generate("evil@example.com");
        var combined = PgpTestKeys.CombineArmoredSignatures(
            stranger.SignDetached(Payload), trusted.SignDetached(Payload));

        var result = AdvisoryVerifier.Verify(Payload, [combined], Trust(trusted), quorumThreshold: 1);

        Assert.True(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        Assert.Contains(result.Signatures,
            s => s.Status == SignatureStatus.ValidTrusted && s.Fingerprint == trusted.Fingerprint);
        Assert.Contains(result.Signatures, s => s.Status == SignatureStatus.UnknownSigner);
    }

    [Fact]
    public void Issuer_key_id_rewritten_to_another_trusted_signer_still_verifies_under_the_real_signer()
    {
        // The Issuer Key ID subpacket of a v4 signature sits in the *unhashed* area - not covered by
        // the signature itself - so a hostile relay can rewrite it with no key material at all.
        // Simulate that: sign genuinely with A, then overwrite the claimed issuer key id to B's.
        // AdvisoryVerifier never consults signature.KeyId to decide trust (VerifyOne brute-forces
        // every trusted key instead), so this must still verify correctly as A - not as B, and not
        // silently disappear the way it would under a KeyId-indexed lookup.
        var a = PgpTestKeys.Generate("a@example.com");
        var b = PgpTestKeys.Generate("b@example.com");
        var genuine = a.SignDetached(Payload);
        var rewritten = PgpTestKeys.RewriteIssuerKeyId(
            genuine, fromKeyId: a.SecretKey.PublicKey.KeyId, toKeyId: b.SecretKey.PublicKey.KeyId);

        var result = AdvisoryVerifier.Verify(Payload, [rewritten], Trust(a, b), quorumThreshold: 1);

        Assert.True(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        var sig = Assert.Single(result.Signatures);
        Assert.Equal(SignatureStatus.ValidTrusted, sig.Status);
        Assert.Equal(a.Fingerprint, sig.Fingerprint); // correctly attributed to the real signer
    }

    // --- Round-2 additions: LoadTrustedKeys now validates subkey-binding signatures (NEW-1), caps
    // the cost of a single armored block (NEW-2), and reports InvalidSignature's fingerprint in the
    // "claimed:" form (finding 3, covered above in Tampered_payload_invalidates_every_signature).

    [Fact]
    public void Stapled_rogue_subkey_with_no_binding_signature_does_not_reach_quorum()
    {
        // Reproduces NEW-1: BouncyCastle does not validate subkey-binding signatures while parsing a
        // ring, so a rogue key stapled onto a victim's otherwise-genuine armored blob - with no
        // binding signature at all - must still be rejected. The victim's own primary must keep
        // working normally; only the stapled addition is untrusted.
        var victim = PgpTestKeys.Generate("victim@example.com");
        var rogue = PgpTestKeys.GenerateWithSigningSubkey("evil@example.com");
        // Labelled by the victim's own real fingerprint (not a placeholder): the round-3
        // ring-to-label cross-check requires this to admit the (still primary-untouched) ring at
        // all - see Stapled_rogue_primary_ring_does_not_reach_quorum for the case where the label
        // is deliberately wrong.
        var poisoned = new Dictionary<string, string>
        {
            [victim.Fingerprint] = PgpTestKeys.StapleRogueSubkey(victim, rogue, includeBindingSignature: false)
        };

        var forged = AdvisoryVerifier.Verify(Payload, [rogue.SignDetached(Payload)], poisoned, quorumThreshold: 1);
        Assert.False(forged.QuorumMet);
        Assert.Equal(0, forged.TrustedValidCount);
        Assert.DoesNotContain(forged.Signatures, s => s.Status == SignatureStatus.ValidTrusted);

        var genuine = AdvisoryVerifier.Verify(Payload, [victim.SignDetached(Payload)], poisoned, quorumThreshold: 1);
        Assert.True(genuine.QuorumMet);
        var sig = Assert.Single(genuine.Signatures);
        Assert.Equal(SignatureStatus.ValidTrusted, sig.Status);
        Assert.Equal(victim.Fingerprint, sig.Fingerprint);
    }

    [Fact]
    public void Stapled_rogue_subkey_with_a_foreign_binding_signature_does_not_reach_quorum()
    {
        // A binding signature that is real - just produced by the attacker's own master key, not
        // the victim's - must be rejected exactly like no binding signature at all. Structurally
        // present is not the same as cryptographically valid against the ring it was stapled onto.
        var victim = PgpTestKeys.Generate("victim@example.com");
        var rogue = PgpTestKeys.GenerateWithSigningSubkey("evil@example.com");
        // Labelled by the victim's own real fingerprint - see the sibling test above.
        var poisoned = new Dictionary<string, string>
        {
            [victim.Fingerprint] = PgpTestKeys.StapleRogueSubkey(victim, rogue, includeBindingSignature: true)
        };

        var forged = AdvisoryVerifier.Verify(Payload, [rogue.SignDetached(Payload)], poisoned, quorumThreshold: 1);
        Assert.False(forged.QuorumMet);
        Assert.Equal(0, forged.TrustedValidCount);
        Assert.DoesNotContain(forged.Signatures, s => s.Status == SignatureStatus.ValidTrusted);

        var genuine = AdvisoryVerifier.Verify(Payload, [victim.SignDetached(Payload)], poisoned, quorumThreshold: 1);
        Assert.True(genuine.QuorumMet);
        var sig = Assert.Single(genuine.Signatures);
        Assert.Equal(SignatureStatus.ValidTrusted, sig.Status);
        Assert.Equal(victim.Fingerprint, sig.Fingerprint);
    }

    [Fact]
    public void Armored_block_over_the_signature_count_cap_is_rejected_as_malformed()
    {
        // 65 copies of one genuine, individually-trusted signature - proves the cap fires on count
        // alone, regardless of whether every individual signature is well-formed and trusted.
        var a = PgpTestKeys.Generate("a@example.com");
        var oneSig = a.SignDetached(Payload);
        var overCap = PgpTestKeys.CombineArmoredSignatures(Enumerable.Repeat(oneSig, 65).ToArray());

        var result = AdvisoryVerifier.Verify(Payload, [overCap], Trust(a), quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        Assert.Equal(SignatureStatus.Malformed, Assert.Single(result.Signatures).Status);
    }

    [Fact]
    public void Armored_block_at_exactly_the_signature_count_cap_is_still_processed()
    {
        // Boundary check: 64 must still be accepted - only exceeding the cap rejects the block.
        var a = PgpTestKeys.Generate("a@example.com");
        var oneSig = a.SignDetached(Payload);
        var atCap = PgpTestKeys.CombineArmoredSignatures(Enumerable.Repeat(oneSig, 64).ToArray());

        var result = AdvisoryVerifier.Verify(Payload, [atCap], Trust(a), quorumThreshold: 1);

        Assert.True(result.QuorumMet);
        Assert.Equal(64, result.Signatures.Count);
        Assert.All(result.Signatures, s => Assert.Equal(SignatureStatus.ValidTrusted, s.Status));
    }

    [Fact]
    public void Armored_block_over_the_byte_length_cap_is_rejected_as_malformed()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var oversized = a.SignDetached(Payload) + new string('X', 70_000);

        var result = AdvisoryVerifier.Verify(Payload, [oversized], Trust(a), quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        Assert.Equal(SignatureStatus.Malformed, Assert.Single(result.Signatures).Status);
    }

    [Fact]
    public void Armored_block_at_exactly_the_byte_length_cap_is_still_processed()
    {
        // Boundary check pinning the exact constant, mirroring the count-cap pair above: exactly at
        // AdvisoryVerifier.MaxArmoredSignatureLength (64 * 1024) must still be processed - padding
        // placed after a complete, genuine armored block is inert (armor decoding stops at the
        // "-----END PGP SIGNATURE-----" marker), matching the technique already proven by the
        // over-cap test above.
        const int cap = 64 * 1024;
        var a = PgpTestKeys.Generate("a@example.com");
        var genuine = a.SignDetached(Payload);
        var atCap = genuine + new string('X', cap - genuine.Length);

        var result = AdvisoryVerifier.Verify(Payload, [atCap], Trust(a), quorumThreshold: 1);

        Assert.True(result.QuorumMet);
        var sig = Assert.Single(result.Signatures);
        Assert.Equal(SignatureStatus.ValidTrusted, sig.Status);
    }

    [Fact]
    public void Armored_block_one_byte_over_the_length_cap_is_rejected_as_malformed()
    {
        const int cap = 64 * 1024;
        var a = PgpTestKeys.Generate("a@example.com");
        var genuine = a.SignDetached(Payload);
        var overCap = genuine + new string('X', cap - genuine.Length + 1);

        var result = AdvisoryVerifier.Verify(Payload, [overCap], Trust(a), quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        Assert.Equal(SignatureStatus.Malformed, Assert.Single(result.Signatures).Status);
    }

    // --- Round-3 additions: NEW-A (a null element in armoredSignatures threw instead of failing
    // closed) and NEW-B (LoadTrustedKeys admitted every ring in a blob with no cross-check against
    // the label it was filed under, so a stapled rogue PRIMARY - which needs no binding signature at
    // all, since a ring's own primary is never gated on one - forged a quorum-counting signature
    // reported under an innocent signer's real fingerprint; cheaper than the round-2 subkey vector).

    [Fact]
    public void Null_element_in_signature_list_is_reported_malformed_and_does_not_throw()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(Payload, [null!], Trust(a), quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        Assert.Equal(SignatureStatus.Malformed, Assert.Single(result.Signatures).Status);
    }

    [Fact]
    public void Null_element_following_a_genuine_signature_does_not_discard_it()
    {
        // A null element used to throw out of Verify entirely, losing every result computed before
        // it, not merely the null entry itself.
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(
            Payload, [a.SignDetached(Payload), null!], Trust(a), quorumThreshold: 1);

        Assert.True(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
        Assert.Equal(2, result.Signatures.Count);
        Assert.Contains(result.Signatures, s => s.Status == SignatureStatus.ValidTrusted);
        Assert.Contains(result.Signatures, s => s.Status == SignatureStatus.Malformed);
    }

    [Fact]
    public void Stapled_rogue_primary_ring_does_not_reach_quorum()
    {
        // NEW-B: a bare, unsigned, user-id-less rogue PRIMARY ring stapled onto the victim's blob -
        // a "Public Key"-tagged packet always starts a NEW ring rather than extending the one before
        // it, so this needs no binding signature, no user id, and no self-signature at all: a ring's
        // own primary is admitted by BuildTrustedRing unconditionally. Only the round-3 ring-to-label
        // cross-check stops it: the rogue ring's own derived fingerprint never matches the label the
        // victim's blob was filed under (the victim's real fingerprint), so it is never even passed
        // to BuildTrustedRing. The victim's own genuine ring, first in the blob and matching the
        // label, must keep working normally.
        var victim = PgpTestKeys.Generate("victim@example.com");
        var rogue = PgpTestKeys.Generate("evil@example.com");
        var poisoned = new Dictionary<string, string>
        {
            [victim.Fingerprint] = PgpTestKeys.CombineArmoredPublicKeys(victim.ArmoredPublicKey, rogue.ArmoredPublicKey)
        };

        var forged = AdvisoryVerifier.Verify(Payload, [rogue.SignDetached(Payload)], poisoned, quorumThreshold: 1);
        Assert.False(forged.QuorumMet);
        Assert.Equal(0, forged.TrustedValidCount);
        Assert.DoesNotContain(forged.Signatures, s => s.Status == SignatureStatus.ValidTrusted);

        var genuine = AdvisoryVerifier.Verify(Payload, [victim.SignDetached(Payload)], poisoned, quorumThreshold: 1);
        Assert.True(genuine.QuorumMet);
        var sig = Assert.Single(genuine.Signatures);
        Assert.Equal(SignatureStatus.ValidTrusted, sig.Status);
        Assert.Equal(victim.Fingerprint, sig.Fingerprint);
    }

    [Fact]
    public void Two_stapled_rogue_primaries_do_not_meet_a_quorum_of_two()
    {
        // Two independently-poisoned trusted entries (e.g. two different signers whose blobs each
        // got poisoned via a different compromised mirror), each rogue key signing - proves the fix
        // isn't merely "reduces the odds", it structurally prevents forgery regardless of how many
        // poisoned entries or forged signers are combined, even at a threshold matching their count.
        var victimA = PgpTestKeys.Generate("victimA@example.com");
        var victimB = PgpTestKeys.Generate("victimB@example.com");
        var rogueA = PgpTestKeys.Generate("evilA@example.com");
        var rogueB = PgpTestKeys.Generate("evilB@example.com");
        var poisoned = new Dictionary<string, string>
        {
            [victimA.Fingerprint] = PgpTestKeys.CombineArmoredPublicKeys(victimA.ArmoredPublicKey, rogueA.ArmoredPublicKey),
            [victimB.Fingerprint] = PgpTestKeys.CombineArmoredPublicKeys(victimB.ArmoredPublicKey, rogueB.ArmoredPublicKey),
        };

        var forged = AdvisoryVerifier.Verify(
            Payload, [rogueA.SignDetached(Payload), rogueB.SignDetached(Payload)], poisoned, quorumThreshold: 2);

        Assert.False(forged.QuorumMet);
        Assert.Equal(0, forged.TrustedValidCount);

        // Non-vacuity: both poisoned entries actually loaded something - the victims' own genuine
        // primaries, cross-check-matched despite the stapled rogue ring alongside each. Without
        // this, a future regression that made both entries load nothing (e.g. a broken cross-check)
        // would leave the assertions above green while proving nothing about rogue primaries
        // specifically - this is exactly the failure mode the round-3 fixture relabelling caught,
        // here pinned explicitly rather than relying on a sibling test to catch it.
        var genuine = AdvisoryVerifier.Verify(
            Payload, [victimA.SignDetached(Payload), victimB.SignDetached(Payload)], poisoned, quorumThreshold: 2);
        Assert.True(genuine.QuorumMet);
        Assert.Equal(2, genuine.TrustedValidCount);
        Assert.Contains(genuine.Signatures,
            s => s.Status == SignatureStatus.ValidTrusted && s.Fingerprint == victimA.Fingerprint);
        Assert.Contains(genuine.Signatures,
            s => s.Status == SignatureStatus.ValidTrusted && s.Fingerprint == victimB.Fingerprint);
    }

    [Fact]
    public void Entry_whose_label_does_not_match_any_ring_in_its_blob_loads_nothing()
    {
        // The cross-check's negative case in isolation, with no staple involved at all: an otherwise
        // completely genuine key, filed under a label that isn't its own fingerprint, must not be
        // trusted - not even under its own correct identity, since nothing here vetted that mapping.
        var a = PgpTestKeys.Generate("a@example.com");
        var mislabeled = new Dictionary<string, string> { ["not-a-real-fingerprint"] = a.ArmoredPublicKey };

        var result = AdvisoryVerifier.Verify(Payload, [a.SignDetached(Payload)], mislabeled, quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        Assert.Equal(0, result.TrustedValidCount);
        Assert.DoesNotContain(result.Signatures, s => s.Status == SignatureStatus.ValidTrusted);
    }

    [Fact]
    public void A_plain_key_entry_and_a_subkey_ring_entry_both_work_together()
    {
        // The cross-check must not break ordinary, non-adversarial multi-entry use: two correctly
        // self-labelled trusted entries (via the existing Trust() helper) - one a bare key, one a
        // real master+subkey ring - both still verify and both count toward quorum.
        var plain = PgpTestKeys.Generate("plain@example.com");
        var withSubkey = PgpTestKeys.GenerateWithSigningSubkey("subkey-owner@example.com");
        var trusted = Trust(plain, withSubkey);

        var result = AdvisoryVerifier.Verify(
            Payload, [plain.SignDetached(Payload), withSubkey.SignDetached(Payload)], trusted, quorumThreshold: 2);

        Assert.True(result.QuorumMet);
        Assert.Equal(2, result.TrustedValidCount);
        Assert.All(result.Signatures, s => Assert.Equal(SignatureStatus.ValidTrusted, s.Status));
    }

    // Not implemented: two trusted keys sharing a 64-bit Key ID. A genuine collision requires an RSA
    // key-generation birthday search infeasible to run inline in a test - the same class of work as
    // the real-world "Evil 32" / 64-bit-id-collision research - and forging one by editing key
    // material directly would produce a key whose declared id doesn't actually match its own
    // content, which is not a real collision, just a lie the test would be telling itself. The
    // try-all-keys design (AdvisoryVerifier.VerifyOne) makes the scenario moot regardless: trusted
    // keys are held in a flat list that is always tried exhaustively, never a KeyId-keyed dictionary
    // slot, so two trusted keys sharing a Key ID cannot shadow one another no matter how the
    // collision was produced.
}
