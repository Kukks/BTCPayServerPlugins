using System.Text;
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
        Assert.All(result.Signatures, s => Assert.True(s.Valid && s.Trusted));
    }

    [Fact]
    public void One_signature_does_not_meet_a_quorum_of_two()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(
            Payload, [a.SignDetached(Payload)], Trust(a), quorumThreshold: 2);

        Assert.False(result.QuorumMet);
        Assert.Equal(1, result.TrustedValidCount);
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
        Assert.All(result.Signatures, s => Assert.False(s.Valid));
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
        Assert.Contains(result.Signatures, s => s.Valid && !s.Trusted);
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
    }

    [Fact]
    public void Malformed_signature_is_reported_invalid_and_does_not_throw()
    {
        var a = PgpTestKeys.Generate("a@example.com");
        var result = AdvisoryVerifier.Verify(
            Payload, ["-----BEGIN PGP SIGNATURE-----\ngarbage\n-----END PGP SIGNATURE-----"],
            Trust(a), quorumThreshold: 1);

        Assert.False(result.QuorumMet);
        Assert.All(result.Signatures, s => Assert.False(s.Valid));
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
}
