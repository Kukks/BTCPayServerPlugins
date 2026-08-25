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
        var poisoned = new Dictionary<string, string>
        {
            ["victim"] = PgpTestKeys.StapleRogueSubkey(victim, rogue, includeBindingSignature: false)
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
        var poisoned = new Dictionary<string, string>
        {
            ["victim"] = PgpTestKeys.StapleRogueSubkey(victim, rogue, includeBindingSignature: true)
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
