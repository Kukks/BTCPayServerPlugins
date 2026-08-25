using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class AdvisoryVerifier
{
    /// <summary>
    /// A real detached signature is a few hundred bytes; this is deliberately generous headroom -
    /// not a realistic size - existing only to bound the cost of a hostile input before any parsing
    /// is attempted at all.
    /// </summary>
    const int MaxArmoredSignatureLength = 64 * 1024;

    /// <summary>
    /// A realistic quorum is a handful of signers. This bounds how many signature packets a single
    /// armored block may contain before <see cref="ParseSignatures"/> rejects the whole block,
    /// rather than brute-force verifying an unbounded number of them against every trusted key (see
    /// <see cref="VerifyOne"/>) - otherwise a hostile block could force unbounded CPU on every call.
    /// </summary>
    const int MaxSignaturesPerBlock = 64;

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

    /// <summary>
    /// Returns the fingerprint of the first ring's primary/master key. Intended for single-key
    /// blobs - one admin-pasted public key at a time - which is the shape this is meant for; it
    /// silently ignores any further rings in a multi-key blob (see <see cref="ReadPublicKeyRing"/>).
    /// The trusted-key store (<see cref="LoadTrustedKeys"/>) does not use this - it honours every
    /// ring in a blob via <see cref="ReadPublicKeyRings"/> instead.
    /// </summary>
    public static string FingerprintOf(string armoredPublicKey)
        => Convert.ToHexString(PrimaryKeyOf(ReadPublicKeyRing(armoredPublicKey)).GetFingerprint());

    /// <summary>
    /// Loads every trusted key ring - every ring in every configured blob, each validated
    /// independently - flattened to the ring's primary (master) fingerprint plus every key in that
    /// ring admitted by <see cref="BuildTrustedRing"/> (the primary always; a subkey only if its
    /// binding signature actually verifies against that primary). There is deliberately no lookup
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
            List<PgpPublicKeyRing> parsedRings;
            try
            {
                parsedRings = ReadPublicKeyRings(armored);
            }
            catch (Exception)
            {
                // The whole entry is unreadable - skip it, never treated as matching.
                continue;
            }

            // Every ring in a multi-key blob is admitted, each validated independently: an admin
            // who pastes a blob containing several keys should not have all but the first silently
            // ignored with no error.
            foreach (var ring in parsedRings)
            {
                try
                {
                    rings.Add(BuildTrustedRing(ring));
                }
                catch (Exception)
                {
                    // This one ring within the bundle is unusable - skip only it.
                }
            }
        }
        return rings;
    }

    /// <summary>
    /// Builds one <see cref="TrustedRing"/> from a parsed key ring: the primary/master key is
    /// always admitted, but a non-primary key (a genuine signing subkey - or, if this blob was
    /// tampered with, a rogue key stapled on to piggyback on the primary's trusted fingerprint) is
    /// admitted only if it carries a subkey-binding signature that cryptographically verifies
    /// against THIS ring's primary. BouncyCastle does not validate binding signatures while parsing
    /// a ring - <see cref="PgpPublicKeyRing.GetPublicKeys"/> returns every key packet the blob
    /// contains, full stop - so skipping this check would let anyone who can influence the imported
    /// blob (a poisoned keyserver mirror, a hostile CDN, a malicious "add a trusted key" PR) forge a
    /// quorum-counting signature reported under an innocent, otherwise-untouched signer's real
    /// fingerprint. A key with zero valid binding signatures is excluded, silently as far as the
    /// caller is concerned - it is simply never added to the ring's key list.
    /// </summary>
    static TrustedRing BuildTrustedRing(PgpPublicKeyRing ring)
    {
        var primary = PrimaryKeyOf(ring);
        var primaryFingerprint = Convert.ToHexString(primary.GetFingerprint());

        var keys = new List<PgpPublicKey> { primary };

        foreach (PgpPublicKey candidate in ring.GetPublicKeys())
        {
            if (candidate.IsMasterKey)
                continue; // Already admitted above, unconditionally.

            if (HasValidSubkeyBinding(primary, candidate))
                keys.Add(candidate);
        }

        return new TrustedRing(primaryFingerprint, keys);
    }

    /// <summary>
    /// True only if <paramref name="candidate"/> carries at least one 0x18 subkey-binding signature
    /// that cryptographically verifies against <paramref name="primary"/> specifically. A binding
    /// signature that is real but was produced by a different key (e.g. an attacker's own master,
    /// stapled alongside their own genuinely-signed subkey) fails this exactly like having no
    /// binding signature at all - "structurally present" is never treated as "valid". Never throws:
    /// a malformed or unreadable binding signature just means that one signature (or that whole
    /// candidate, if none of its binding signatures are usable) is rejected, not that
    /// <see cref="LoadTrustedKeys"/> aborts.
    /// </summary>
    static bool HasValidSubkeyBinding(PgpPublicKey primary, PgpPublicKey candidate)
    {
        try
        {
            foreach (PgpSignature bindingSignature in candidate.GetSignaturesOfType(PgpSignature.SubkeyBinding))
            {
                try
                {
                    bindingSignature.InitVerify(primary);
                    if (bindingSignature.VerifyCertification(primary, candidate))
                        return true;
                }
                catch (Exception)
                {
                    // This particular binding signature is unusable against this primary - try any
                    // others the candidate carries before giving up on it.
                }
            }
        }
        catch (Exception)
        {
            // No usable binding signatures at all (or reading them failed outright) - reject.
        }
        return false;
    }

    /// <summary>
    /// Parses every signature packet out of one armored block, draining the whole object stream -
    /// not just the first object found - so a second signature list later in the same block (a
    /// multi-signature .asc file) is never missed.
    ///
    /// Does NOT reliably keep already-parsed signatures when a later packet in the same block is
    /// malformed. BouncyCastle's <see cref="PgpObjectFactory"/> greedily coalesces a run of
    /// consecutive signature packets into a single <see cref="PgpSignatureList"/> before ever
    /// returning it, so a corrupt or truncated signature packet immediately following genuine ones
    /// throws while that whole run is still being assembled - before any of it, including the
    /// genuine signatures ahead of the corrupt one, is ever handed back to us. A single small
    /// corrupt packet appended to a genuine multi-signature run can therefore suppress the entire
    /// run. This is pre-existing behaviour, not introduced by the multi-signature support added
    /// here - the original single-signature implementation had the identical failure mode - and it
    /// fails closed (the whole block reports Malformed via <see cref="Verify"/>), so the practical
    /// consequence is a kill switch that goes inert rather than one that is fooled. It remains a
    /// real, unresolved gap worth being on the record. Only a non-signature packet (or the stream
    /// simply ending) between two runs of signature packets protects an earlier, already-returned
    /// <see cref="PgpSignatureList"/> from a later run's failure.
    ///
    /// Bounded defensively: an armored block over <see cref="MaxArmoredSignatureLength"/> bytes, or
    /// containing more than <see cref="MaxSignaturesPerBlock"/> signature packets, is rejected
    /// outright (returns empty, so the caller reports Malformed) rather than parsed.
    /// </summary>
    static List<PgpSignature> ParseSignatures(string armoredSignature)
    {
        var signatures = new List<PgpSignature>();

        if (armoredSignature.Length > MaxArmoredSignatureLength)
            return signatures;

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
                {
                    if (signatures.Count >= MaxSignaturesPerBlock)
                        return new List<PgpSignature>(); // Over cap: reject the whole block.
                    signatures.Add(list[i]);
                }
            }
        }
        catch (Exception)
        {
            // Keep whatever was already parsed before the failure - see the doc comment above for
            // why this is weaker protection than it may look for a corrupt packet that immediately
            // follows genuine ones in the same run.
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
        // of identity, only used to pick a more informative label for an already-failed result -
        // and the fingerprint is reported in the "claimed:" form, never bare, so it can never be
        // mistaken for a verified attribution (see SignatureResult.Fingerprint).
        foreach (var ring in trustedRings)
        {
            foreach (var key in ring.Keys)
            {
                if (key.KeyId == signature.KeyId)
                    return new SignatureResult($"claimed:{ring.PrimaryFingerprint}", SignatureStatus.InvalidSignature);
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

    /// <summary>
    /// Returns only the first ring in the armored blob. See <see cref="FingerprintOf"/>, its only
    /// caller.
    /// </summary>
    static PgpPublicKeyRing ReadPublicKeyRing(string armoredPublicKey)
    {
        foreach (var ring in ReadPublicKeyRings(armoredPublicKey))
            return ring;
        throw new InvalidDataException("No usable public key ring found in armored block.");
    }

    /// <summary>Returns every ring found in the armored blob. See <see cref="LoadTrustedKeys"/>, its
    /// only caller.</summary>
    static List<PgpPublicKeyRing> ReadPublicKeyRings(string armoredPublicKey)
    {
        using var input = PgpUtilities.GetDecoderStream(
            new MemoryStream(Encoding.ASCII.GetBytes(armoredPublicKey)));
        var bundle = new PgpPublicKeyRingBundle(input);

        var rings = new List<PgpPublicKeyRing>();
        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
            rings.Add(ring);

        if (rings.Count == 0)
            throw new InvalidDataException("No usable public key ring found in armored block.");
        return rings;
    }

    sealed record TrustedRing(string PrimaryFingerprint, List<PgpPublicKey> Keys);
}
