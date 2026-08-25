using System.Collections.Generic;

namespace BTCPayServer.Plugins.SecSwitch.Models;

public sealed record SignatureResult(string Fingerprint, bool Valid, bool Trusted);

public sealed record VerificationResult(
    bool QuorumMet,
    int TrustedValidCount,
    int Required,
    IReadOnlyList<SignatureResult> Signatures);
