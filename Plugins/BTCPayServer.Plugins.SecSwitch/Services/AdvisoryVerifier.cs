using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class AdvisoryVerifier
{
    public static VerificationResult Verify(
        byte[] payload,
        IReadOnlyList<string> armoredSignatures,
        IReadOnlyDictionary<string, string> trustedKeysByFingerprint,
        int quorumThreshold)
    {
        var required = Math.Max(1, quorumThreshold);

        // Defensive guards: the parameters are typed non-nullable, but nullable reference types are
        // a compile-time hint only, not a runtime guarantee - a caller across an assembly boundary,
        // or a defensive-coding slip, can still pass null. This is the security core of the plugin:
        // a thrown exception here must never be mistaken by an upstream caller for "verification
        // succeeded" (e.g. a try/catch that swallows and defaults to allowing an action), so every
        // unusable input fails closed to "quorum not met" rather than throwing. Mirrors the same
        // defensive pattern used in AdvisoryApplicability.IsApplicable.
        if (payload is null || armoredSignatures is null || trustedKeysByFingerprint is null)
            return new VerificationResult(false, 0, required, Array.Empty<SignatureResult>());

        var trustedKeys = LoadTrustedKeys(trustedKeysByFingerprint);
        var results = new List<SignatureResult>();
        var countedFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var armored in armoredSignatures)
        {
            var result = VerifyOne(payload, armored, trustedKeys);
            results.Add(result);
            if (result is { Valid: true, Trusted: true })
                countedFingerprints.Add(result.Fingerprint);
        }

        return new VerificationResult(
            QuorumMet: countedFingerprints.Count >= required,
            TrustedValidCount: countedFingerprints.Count,
            Required: required,
            Signatures: results);
    }

    public static string FingerprintOf(string armoredPublicKey)
        => Convert.ToHexString(ReadPublicKey(armoredPublicKey).GetFingerprint());

    static Dictionary<long, (string Fingerprint, PgpPublicKey Key)> LoadTrustedKeys(
        IReadOnlyDictionary<string, string> trustedKeysByFingerprint)
    {
        var byKeyId = new Dictionary<long, (string, PgpPublicKey)>();

        // The caller-supplied dictionary key (the fingerprint label under which the admin filed this
        // trusted key) is intentionally never read - see the discard below. Each key's fingerprint is
        // instead re-derived from the key material itself via GetFingerprint(). Trusting the caller's
        // label would let a mislabelled entry (right key material filed under the wrong fingerprint,
        // or vice versa) silently verify under an identity it doesn't actually have.
        foreach (var (_, armored) in trustedKeysByFingerprint)
        {
            try
            {
                var key = ReadPublicKey(armored);
                // Index by key id because that is what a signature carries; compare fingerprints after.
                byKeyId[key.KeyId] = (Convert.ToHexString(key.GetFingerprint()), key);
            }
            catch (Exception)
            {
                // An unreadable trusted key is skipped, never treated as matching.
            }
        }
        return byKeyId;
    }

    static SignatureResult VerifyOne(
        byte[] payload, string armoredSignature,
        Dictionary<long, (string Fingerprint, PgpPublicKey Key)> trustedKeys)
    {
        PgpSignature? signature = null;
        try
        {
            using var input = PgpUtilities.GetDecoderStream(
                new MemoryStream(Encoding.ASCII.GetBytes(armoredSignature)));
            var factory = new PgpObjectFactory(input);
            var pgpObject = factory.NextPgpObject();
            if (pgpObject is PgpSignatureList list && list.Count > 0)
                signature = list[0];
        }
        catch (Exception)
        {
            return new SignatureResult("", false, false);
        }

        if (signature is null)
            return new SignatureResult("", false, false);

        if (!trustedKeys.TryGetValue(signature.KeyId, out var trusted))
        {
            // The signing key is not in the trusted set, so we hold no public key to
            // cryptographically check this signature against - that check is impossible without it.
            // "Valid: true" here means only that the input parsed above as a structurally well-formed
            // OpenPGP signature packet; it is never a claim of cryptographic validity. Trusted is
            // always false in this branch, and Verify() above only adds a fingerprint to
            // countedFingerprints when a result is both Valid AND Trusted, so a result from this
            // branch can never contribute to quorum no matter what this literal is set to.
            const bool structurallyPresentButUnverified = true;
            return new SignatureResult($"keyid:{signature.KeyId:X16}", structurallyPresentButUnverified, false);
        }

        try
        {
            signature.InitVerify(trusted.Key);
            signature.Update(payload);
            return new SignatureResult(trusted.Fingerprint, signature.Verify(), true);
        }
        catch (Exception)
        {
            return new SignatureResult(trusted.Fingerprint, false, true);
        }
    }

    static PgpPublicKey ReadPublicKey(string armoredPublicKey)
    {
        using var input = PgpUtilities.GetDecoderStream(
            new MemoryStream(Encoding.ASCII.GetBytes(armoredPublicKey)));
        var bundle = new PgpPublicKeyRingBundle(input);
        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpPublicKey key in ring.GetPublicKeys())
            {
                if (key.IsMasterKey || key.IsEncryptionKey == false)
                    return key;
            }
        }
        throw new InvalidDataException("No usable public key found in armored block.");
    }
}
