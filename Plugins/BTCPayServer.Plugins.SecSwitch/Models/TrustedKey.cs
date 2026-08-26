namespace BTCPayServer.Plugins.SecSwitch.Models;

/// <summary>
/// One trusted signing key in the trust store. <see cref="Fingerprint"/> must always be the value
/// <see cref="Services.AdvisoryVerifier.FingerprintOf"/> derives from <see cref="ArmoredPublicKey"/>
/// itself - never an admin-typed or otherwise independently supplied string - because
/// <see cref="Services.AdvisoryVerifier.Verify"/> only admits a trusted entry whose own re-derived
/// primary fingerprint matches the dictionary label it is filed under; a mismatched fingerprint
/// here would silently make the entry contribute nothing to quorum.
///
/// There are THREE places a <see cref="TrustedKey"/> is constructed from admin, rotation, or bundled
/// input, and every one of them must re-derive the fingerprint the same way (final whole-branch
/// review, Finding M3 - this comment previously named <see cref="Services.TrustStore"/> as "the only
/// place", which stopped being true once the other two were added):
/// <see cref="Services.TrustStore.TryApplyRotation"/> (a quorum-signed rotation document),
/// <c>SecSwitchController.AddTrustedKey</c> (an admin's raw paste), and
/// <see cref="Services.TrustRootBootstrapper"/> (the embedded trust-root resource). All three call
/// <see cref="Services.AdvisoryVerifier.FingerprintOf"/> on the key material itself.
/// </summary>
public sealed class TrustedKey
{
    public string Fingerprint { get; set; } = "";
    public string ArmoredPublicKey { get; set; } = "";
    public string Identity { get; set; } = "";
}

/// <summary>
/// A quorum-signed request to add and/or remove trusted keys. <see cref="Add"/> holds armored
/// public keys - their fingerprints are always re-derived, never trusted from any other source
/// (see <see cref="Services.TrustStore"/>); <see cref="Remove"/> holds fingerprints of keys to drop.
/// </summary>
public sealed class KeyRotation
{
    public string[] Add { get; set; } = [];
    public string[] Remove { get; set; } = [];
}
