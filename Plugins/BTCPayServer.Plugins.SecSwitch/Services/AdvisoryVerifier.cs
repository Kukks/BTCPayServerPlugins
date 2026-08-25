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

        var trustedRings = LoadTrustedKeys(trustedKeysByFingerprint);
        var results = new List<SignatureResult>();
        var countedFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var armored in armoredSignatures)
        {
            var parsed = ParseSignatures(armored);
            if (parsed.Count == 0)
            {
                // Nothing recognizable as a signature packet came out of this block at all.
                results.Add(new SignatureResult("", SignatureStatus.Malformed));
                continue;
            }

            // One armored block can legitimately carry more than one signature packet (e.g. a
            // maintainer who ran `gpg --detach-sign` twice into the same .asc file) - every one of
            // them gets its own result, and quorum dedup below still applies per fingerprint.
            foreach (var signature in parsed)
            {
                var result = VerifyOne(payload, signature, trustedRings);
                results.Add(result);
                if (result.Status == SignatureStatus.ValidTrusted)
                    countedFingerprints.Add(result.Fingerprint);
            }
        }

        return new VerificationResult(
            QuorumMet: countedFingerprints.Count >= required,
            TrustedValidCount: countedFingerprints.Count,
            Required: required,
            Signatures: results);
    }

    public static string FingerprintOf(string armoredPublicKey)
        => Convert.ToHexString(PrimaryKeyOf(ReadPublicKeyRing(armoredPublicKey)).GetFingerprint());

    /// <summary>
    /// Loads every trusted key ring, flattened to the ring's primary (master) fingerprint plus
    /// every key in that ring - master and signing subkeys alike. There is deliberately no lookup
    /// keyed by 64-bit Key ID here (see VerifyOne): that field lives in a signature's unhashed area
    /// and is attacker-malleable, so a single dictionary slot indexed by it would let a rewritten
    /// claim shadow the real signer, and two trusted keys that happened to share an id would have
    /// one silently overwrite the other. A flat list makes both impossible - every candidate key is
    /// always tried, never selected via untrusted data.
    /// </summary>
    static List<TrustedRing> LoadTrustedKeys(IReadOnlyDictionary<string, string> trustedKeysByFingerprint)
    {
        var rings = new List<TrustedRing>();

        // The caller-supplied dictionary key (the fingerprint label under which the admin filed
        // this trusted key) is intentionally never read - see the discard below. Each ring's
        // identity is instead re-derived from its own master key material via GetFingerprint().
        // Trusting the caller's label would let a mislabelled entry silently verify under an
        // identity it doesn't hold.
        foreach (var (_, armored) in trustedKeysByFingerprint)
        {
            try
            {
                var ring = ReadPublicKeyRing(armored);
                var primaryFingerprint = Convert.ToHexString(PrimaryKeyOf(ring).GetFingerprint());

                var keys = new List<PgpPublicKey>();
                foreach (PgpPublicKey key in ring.GetPublicKeys())
                    keys.Add(key);

                rings.Add(new TrustedRing(primaryFingerprint, keys));
            }
            catch (Exception)
            {
                // An unreadable trusted key is skipped, never treated as matching.
            }
        }
        return rings;
    }

    /// <summary>
    /// Parses every signature packet out of one armored block, draining the whole object stream -
    /// not just the first object found - so a second signature list later in the same block (a
    /// multi-signature .asc file) is never missed. If a later packet is malformed, whatever
    /// signatures were already read successfully are still returned rather than discarded: a
    /// trailing corrupt packet must not erase evidence that was already validly parsed.
    /// </summary>
    static List<PgpSignature> ParseSignatures(string armoredSignature)
    {
        var signatures = new List<PgpSignature>();
        try
        {
            using var input = PgpUtilities.GetDecoderStream(
                new MemoryStream(Encoding.ASCII.GetBytes(armoredSignature)));
            var factory = new PgpObjectFactory(input);

            PgpObject? pgpObject;
            while ((pgpObject = factory.NextPgpObject()) != null)
            {
                if (pgpObject is not PgpSignatureList list)
                    continue; // Not a signature packet (e.g. a stray key) - nothing to evaluate.

                for (var i = 0; i < list.Count; i++)
                    signatures.Add(list[i]);
            }
        }
        catch (Exception)
        {
            // Keep whatever was already parsed before the failure.
        }
        return signatures;
    }

    static SignatureResult VerifyOne(byte[] payload, PgpSignature signature, List<TrustedRing> trustedRings)
    {
        // Try every key - master and subkey alike - of every trusted ring, until one verifies. This
        // never consults signature.KeyId: in the v4 signatures this code handles, that field lives
        // in the *unhashed* subpacket area, which is not covered by the signature itself, so it can
        // be rewritten in transit with no key material at all. Brute-forcing every trusted key
        // instead of trusting that claim means: a signing subkey verifies exactly like a master key
        // would (no separate subkey lookup to forget); a rewritten issuer id gains an attacker
        // nothing, since the real signer's key still gets tried regardless of what the packet
        // claims; and there is no single dictionary slot for two different trusted keys to collide
        // over. At a realistic quorum size (a handful of trusted keys) this costs nothing.
        foreach (var ring in trustedRings)
        {
            foreach (var key in ring.Keys)
            {
                try
                {
                    signature.InitVerify(key);
                    signature.Update(payload);
                    if (signature.Verify())
                        return new SignatureResult(ring.PrimaryFingerprint, SignatureStatus.ValidTrusted);
                }
                catch (Exception)
                {
                    // This candidate key does not apply to this signature (e.g. an algorithm
                    // mismatch) - move on and try the next one.
                }
            }
        }

        // No trusted key verified this signature. Purely as a diagnostic from here on - this can
        // never change the quorum decision above, which is already final by this point - check
        // whether the signature's own claimed issuer key id happens to name a key we trust, so the
        // report can distinguish "looks tampered" from "signed by someone we don't know at all".
        // Because that claimed id is unhashed and attacker-malleable, it is never treated as proof
        // of identity, only used to pick a more informative label for an already-failed result.
        foreach (var ring in trustedRings)
        {
            foreach (var key in ring.Keys)
            {
                if (key.KeyId == signature.KeyId)
                    return new SignatureResult(ring.PrimaryFingerprint, SignatureStatus.InvalidSignature);
            }
        }

        return new SignatureResult($"keyid:{signature.KeyId:X16}", SignatureStatus.UnknownSigner);
    }

    /// <summary>
    /// The ring's primary/master key, deterministically - not "whichever key happens to satisfy a
    /// filter first". <see cref="PgpPublicKeyRing.GetPublicKey()"/> (no-arg) is BouncyCastle's own
    /// accessor for exactly this.
    /// </summary>
    static PgpPublicKey PrimaryKeyOf(PgpPublicKeyRing ring) => ring.GetPublicKey();

    static PgpPublicKeyRing ReadPublicKeyRing(string armoredPublicKey)
    {
        using var input = PgpUtilities.GetDecoderStream(
            new MemoryStream(Encoding.ASCII.GetBytes(armoredPublicKey)));
        var bundle = new PgpPublicKeyRingBundle(input);
        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
            return ring;
        throw new InvalidDataException("No usable public key ring found in armored block.");
    }

    sealed record TrustedRing(string PrimaryFingerprint, List<PgpPublicKey> Keys);
}
