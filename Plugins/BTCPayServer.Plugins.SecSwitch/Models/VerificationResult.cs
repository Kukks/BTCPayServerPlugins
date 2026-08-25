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

public sealed record SignatureResult(string Fingerprint, SignatureStatus Status);

public sealed record VerificationResult(
    bool QuorumMet,
    int TrustedValidCount,
    int Required,
    IReadOnlyList<SignatureResult> Signatures);
