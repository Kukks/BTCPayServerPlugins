using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public enum SignatureStatus
{
    /// <summary>Could not be parsed as an OpenPGP signature packet at all.</summary>
    Malformed,

    /// <summary>Parsed successfully, but no trusted key (master or subkey, from any trusted key
    /// ring) verified it. This makes no claim about cryptographic validity either way - it is
    /// evidence only that we hold no key this checks out against, never evidence of tampering.</summary>
    UnknownSigner,

    /// <summary>A trusted key was identified as the plausible signer, but cryptographic
    /// verification against it failed (tampering, or a mismatched key).</summary>
    InvalidSignature,

    /// <summary>Cryptographically verified against a trusted key. Only signatures with this status
    /// count toward quorum.</summary>
    ValidTrusted
}

/// <param name="Fingerprint">
/// One of four distinct shapes depending on <see cref="Status"/> - a consumer (e.g. an
/// admin-facing evidence display) MUST NOT treat them as interchangeable:
/// <list type="bullet">
/// <item><description><see cref="SignatureStatus.Malformed"/>: empty string. There is nothing to
/// attribute - the input did not even parse as a signature packet.</description></item>
/// <item><description><see cref="SignatureStatus.UnknownSigner"/>: <c>keyid:XXXXXXXXXXXXXXXX</c>
/// - the signature's own claimed 64-bit issuer key id. That field lives in a v4 signature's
/// unhashed area, so it is attacker-controlled and not cryptographically bound to anything; it
/// does not name any trusted key.</description></item>
/// <item><description><see cref="SignatureStatus.InvalidSignature"/>: <c>claimed:XXXX...</c> (40
/// hex) - the real fingerprint of a trusted key whose identity the signature claims, but
/// cryptographic verification against it failed. Still only a claim, never a verified fact - do
/// not render it as if it were one.</description></item>
/// <item><description><see cref="SignatureStatus.ValidTrusted"/>: a bare 40-hex uppercase
/// fingerprint, with no prefix - the only shape that represents an actual, cryptographically
/// verified attribution.</description></item>
/// </list>
/// </param>
public sealed record SignatureResult(string Fingerprint, SignatureStatus Status);

public sealed record VerificationResult(
    bool QuorumMet,
    int TrustedValidCount,
    int Required,
    IReadOnlyList<SignatureResult> Signatures);
