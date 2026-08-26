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

    /// <summary>
    /// A real trusted-key blob (a primary plus a handful of subkeys, armored) is a few KB at most;
    /// this is deliberately generous headroom, bounding the cost of parsing and ring-matching a
    /// hostile or oversized blob before any of that work is attempted.
    /// </summary>
    const int MaxTrustedKeyBlobLength = 256 * 1024;

    /// <summary>
    /// A real deployment has a ring's primary plus a small number of signing subkeys (one, or a
    /// handful during rotation overlap) - not dozens. This bounds how many non-primary candidates
    /// <see cref="BuildTrustedRing"/> will run <see cref="HasValidSubkeyBinding"/>'s RSA
    /// verification against for a single ring, so a ring stuffed with many candidate keys cannot
    /// force unbounded CPU on every <see cref="Verify"/> call. The round-3 ring-to-label
    /// cross-check in <see cref="TryLoadTrustedKey"/> already limits a blob to at most one admitted
    /// ring; this caps the remaining cost within that one ring. NOTE: this cap is invisible to
    /// <see cref="TryLoadTrustedKey"/> itself - it only governs which of an admitted ring's
    /// non-primary keys <see cref="BuildTrustedRing"/> goes on to examine, not whether the ring is
    /// admitted at all - so a ring whose intended signing subkey happens to fall past candidate 16
    /// in packet order is silently never admitted for that subkey specifically (the primary is
    /// unaffected either way). Task 5's rotation-admission probe inherits this same blind spot; see
    /// Services.TrustStore's report.
    /// </summary>
    const int MaxSubkeyCandidatesPerRing = 16;

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
    /// Loads every trusted entry, cross-checked against the label it was filed under: for each
    /// dictionary entry, every ring in the blob is parsed, but only the ring whose OWN primary
    /// fingerprint (re-derived from its key material - never trusted from the label itself) equals
    /// the dictionary key is admitted, via <see cref="BuildTrustedRing"/> (the primary always; a
    /// non-primary key only if its binding signature verifies against that primary). If no ring in
    /// the blob matches the label, the whole entry contributes nothing.
    ///
    /// This cross-check is NOT "trusting the caller's label": the label is never used as the
    /// reported signer identity (that is always the freshly re-derived fingerprint) and never
    /// substitutes for the binding-signature check on non-primary keys - it only decides which of
    /// possibly several rings in one blob is the one the admin actually vetted under this entry.
    /// Without it, a blob was not required to contain only the ring an admin thinks they vetted:
    /// BouncyCastle's parser has no concept of "the one true ring" for an entry, so a second ring
    /// stapled onto the blob - including a bare, unsigned, user-id-less primary key, which needs no
    /// binding signature at all because a ring's own primary is never gated on one - was admitted
    /// just as fully as the genuine one, forging a quorum-counting signature reported under an
    /// innocent signer's own real, correctly-published fingerprint.
    ///
    /// There is deliberately no lookup keyed by 64-bit Key ID here (see VerifyOne): that field lives
    /// in a signature's unhashed area and is attacker-malleable, so a single dictionary slot indexed
    /// by it would let a rewritten claim shadow the real signer, and two trusted keys that happened
    /// to share an id would have one silently overwrite the other. A flat list of admitted rings
    /// makes both impossible - every candidate key is always tried, never selected via untrusted
    /// data.
    /// </summary>
    static List<TrustedRing> LoadTrustedKeys(IReadOnlyDictionary<string, string> trustedKeysByFingerprint)
    {
        var rings = new List<TrustedRing>();

        foreach (var (label, armored) in trustedKeysByFingerprint)
        {
            if (!TryLoadTrustedKey(label, armored, out var matched) || matched is null)
                continue; // Not admitted under this label - see TryLoadTrustedKey.

            try
            {
                rings.Add(BuildTrustedRing(matched));
            }
            catch (Exception)
            {
                // The matched ring itself turned out to be unusable - skip it.
            }
        }
        return rings;
    }

    /// <summary>
    /// Admission-only probe, factored out of <see cref="LoadTrustedKeys"/> so it and
    /// <see cref="Services.TrustStore"/> can never drift apart (Task 5 review, Finding 1): true only
    /// if a trusted-key dictionary entry filed under <paramref name="label"/> with armored blob
    /// <paramref name="armored"/> would actually be admitted here - the same byte-length cap, ring
    /// parsing, and label cross-check <see cref="LoadTrustedKeys"/> itself relies on, with
    /// <paramref name="matchedRing"/> set to the one ring in the blob whose own re-derived primary
    /// fingerprint matched <paramref name="label"/>.
    ///
    /// Deliberately stops short of <see cref="BuildTrustedRing"/>'s subkey-binding validation -
    /// that question (which of a ring's non-primary keys may also sign) has no bearing on whether
    /// the entry is admitted at all: a ring's primary is unconditionally admitted once matched,
    /// regardless of which (if any) of its subkeys later turn out to have valid bindings.
    ///
    /// internal, not public: this only needs to be visible to <see cref="Services.TrustStore"/>, in
    /// the same assembly, and leaks a BouncyCastle type (<see cref="PgpPublicKeyRing"/>) that has no
    /// business on this plugin's public surface. TrustStore.TryApplyRotation calls this before
    /// admitting a key during rotation, so a blob this method would silently skip (e.g. one over
    /// <see cref="MaxTrustedKeyBlobLength"/>) can never be stored as "trusted" while actually being
    /// permanently unusable for a future <see cref="Verify"/> call - without a single shared
    /// definition of "admitted", TrustStore would have to reimplement these same checks, and any
    /// future drift between the two copies would let a rotation silently and permanently brick the
    /// trust store (a key correctly fingerprinted and stored, but that <see cref="LoadTrustedKeys"/>
    /// can never actually load again).
    /// </summary>
    internal static bool TryLoadTrustedKey(string label, string armored, out PgpPublicKeyRing? matchedRing)
    {
        matchedRing = null;

        // Bound the cost of a hostile or oversized blob before attempting to parse it at all.
        if (armored is null || armored.Length > MaxTrustedKeyBlobLength)
            return false;

        List<PgpPublicKeyRing> parsedRings;
        try
        {
            parsedRings = ReadPublicKeyRings(armored);
        }
        catch (Exception)
        {
            return false; // The whole entry is unreadable - skip it, never treated as matching.
        }

        // Find the one ring in this blob whose own derived fingerprint matches the label the admin
        // filed it under. A blob may contain more than one ring (see ReadPublicKeyRings) - anything
        // that does not match the label is not the ring this entry vetted, so it is never even
        // passed to BuildTrustedRing, let alone admitted.
        foreach (var candidateRing in parsedRings)
        {
            string candidateFingerprint;
            try
            {
                candidateFingerprint = Convert.ToHexString(PrimaryKeyOf(candidateRing).GetFingerprint());
            }
            catch (Exception)
            {
                continue; // This ring's own primary is unreadable - it can never match a label.
            }

            if (string.Equals(candidateFingerprint, label, StringComparison.OrdinalIgnoreCase))
            {
                matchedRing = candidateRing;
                return true;
            }
        }

        return false; // No ring in this blob matches the label it was filed under.
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

        var candidatesChecked = 0;
        foreach (PgpPublicKey candidate in ring.GetPublicKeys())
        {
            if (candidate.IsMasterKey)
                continue; // The ring's primary was already admitted above via
                          // PrimaryKeyOf/GetPublicKey() - this only avoids re-adding it here. (Also
                          // true if a ring ever yielded more than one master-flagged key: any
                          // further one is excluded too, never examined as a binding candidate -
                          // fail-closed, not "impossible".)

            if (candidatesChecked >= MaxSubkeyCandidatesPerRing)
                break; // Cap reached - see MaxSubkeyCandidatesPerRing's doc comment. Any further
                       // candidates in this ring's packet order, including a genuine subkey, are
                       // never examined: fails closed (a signer's own contribution might not
                       // count), never opens a path to admitting more than the cap.
            candidatesChecked++;

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

        if (armoredSignature is null || armoredSignature.Length > MaxArmoredSignatureLength)
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
