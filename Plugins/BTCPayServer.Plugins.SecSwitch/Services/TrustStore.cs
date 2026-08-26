using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// Applies quorum-signed key-rotation documents to the trust store: adding or removing trusted
/// signing keys. This is the most security-sensitive surface in the plugin - a flaw here (e.g.
/// admitting a key that authorised its own addition, or partially applying a rotation that failed
/// partway through) defeats every other control, since <see cref="AdvisoryVerifier"/>'s quorum
/// check is only ever as trustworthy as the set of keys it is told to trust.
/// </summary>
public static class TrustStore
{
    /// <summary>
    /// Applies a key-rotation document to <paramref name="current"/>, producing <paramref name="updated"/>
    /// only if the rotation is validly signed by a quorum of the CURRENT trust set - never the set
    /// as it would look after the rotation is applied, so a key being added can never count toward
    /// authorising its own addition (nor can a key being removed vote to save itself).
    ///
    /// On every failure path <paramref name="updated"/> is an unmodified copy of <paramref name="current"/>
    /// - never a partial application of some but not all of the rotation's changes - and this method
    /// never throws, for any input, including null arguments.
    /// </summary>
    public static bool TryApplyRotation(
        IReadOnlyList<TrustedKey> current,
        byte[] rotationJson,
        IReadOnlyList<string> armoredSignatures,
        int quorumThreshold,
        out List<TrustedKey> updated,
        out string error)
    {
        // Defensive guards mirroring AdvisoryVerifier.Verify's posture: the parameters are typed
        // non-nullable, but that is a compile-time hint only - a caller across an assembly boundary
        // can still pass null, and a null/malformed element of `current` is tolerated the same way.
        // `updated` is set to a safe, filtered snapshot up front and is only ever replaced with the
        // rotated result on the single success path at the very end of this method - every other
        // return (every failure branch below) leaves it exactly as set here, so a caller can never
        // observe a partially-applied rotation via the out parameter, even one that skips checking
        // the returned bool.
        var safeCurrent = (current ?? Array.Empty<TrustedKey>()).Where(k => k is not null).ToList();
        updated = safeCurrent.ToList();
        error = "";

        // Build the CURRENT trust dictionary defensively: a raw ToDictionary would itself throw on
        // a null Fingerprint, or on two entries sharing one (case-insensitively) - both reachable
        // only if something upstream already corrupted the store, but "never throw for any input"
        // makes no exception for that. Last-write-wins on a duplicate, silently - there is no
        // correct disambiguation available here, and TryApplyRotation itself never produces such a
        // duplicate (see the "already trusted" check further down).
        var trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in safeCurrent)
        {
            if (key.Fingerprint is null || key.ArmoredPublicKey is null)
                continue;
            trusted[key.Fingerprint] = key.ArmoredPublicKey;
        }

        // Verify against the CURRENT trust set only - see the security note in the doc comment
        // above. Wrapped defensively even though AdvisoryVerifier.Verify is documented to fail
        // closed rather than throw - "never throw" here has no carve-out for a downstream bug.
        VerificationResult verification;
        try
        {
            verification = AdvisoryVerifier.Verify(rotationJson, armoredSignatures, trusted, quorumThreshold);
        }
        catch (Exception e)
        {
            error = $"Rotation verification failed: {e.Message}";
            return false;
        }

        if (!verification.QuorumMet)
        {
            error = $"Key rotation did not meet quorum ({verification.TrustedValidCount}/{verification.Required}).";
            return false;
        }

        KeyRotation? rotation;
        try
        {
            rotation = JsonSerializer.Deserialize<KeyRotation>(rotationJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e)
        {
            // Catches more broadly than JsonException: "never throw" has no carve-out for an
            // unusual failure mode (e.g. a pathological byte sequence) surfacing as a different
            // exception type from System.Text.Json.
            error = $"Malformed rotation JSON: {e.Message}";
            return false;
        }
        if (rotation is null)
        {
            error = "Empty rotation document.";
            return false;
        }

        // From here on, mutate a working copy rather than `updated` directly - see the doc comment
        // on `updated` above. `updated` is only ever assigned the finished result, right before the
        // single successful return at the bottom.
        var working = safeCurrent.ToList();

        foreach (var fingerprint in rotation.Remove ?? Array.Empty<string>())
        {
            working.RemoveAll(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));
        }

        // Review Minor 4: a rotation's `remove` array shows a human-readable fingerprint, but its
        // `add` array shows an opaque armored blob - a reviewing signer who trusts what `remove`
        // visibly says could be co-signing what reads as "revoke K" while Remove-then-Add ordering
        // would actually leave K trusted (removed above, then immediately re-admitted by the
        // matching Add entry below). Reject the whole rotation outright rather than defining a
        // precedence rule for an author's (possibly malicious) self-contradictory document.
        var removeFingerprints = new HashSet<string>(
            rotation.Remove ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var armored in rotation.Add ?? Array.Empty<string>())
        {
            // CRITICAL CONTRACT: the fingerprint stored for a newly admitted key must be exactly
            // what AdvisoryVerifier.FingerprintOf returns for it - never a value derived any other
            // way - because AdvisoryVerifier.Verify only admits a trusted-key dictionary entry
            // whose re-derived primary fingerprint equals its own dictionary label (see
            // AdvisoryVerifier.LoadTrustedKeys). Storing anything else here would silently create a
            // trusted entry that can never actually count toward a future quorum.
            string fingerprint;
            try
            {
                fingerprint = AdvisoryVerifier.FingerprintOf(armored);
            }
            catch (Exception e)
            {
                error = $"Rotation contained an unreadable public key: {e.Message}";
                return false;
            }

            if (removeFingerprints.Contains(fingerprint))
            {
                error = $"Rotation both adds and removes the same key ({fingerprint}); refusing " +
                        "rather than guessing which was intended.";
                return false;
            }

            if (working.Any(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                continue; // Already trusted - idempotent no-op, and deliberately not re-screened
                          // below: retroactively scanning an already-trusted key for revocation is
                          // a separate, out-of-scope concern (see the comment on the checks below).

            // Review Finding 1: a blob AdvisoryVerifier.FingerprintOf can parse is not necessarily
            // one AdvisoryVerifier.LoadTrustedKeys would ever actually load (e.g. one over its own
            // byte-length cap) - admitting it here anyway would store a *correct* fingerprint for a
            // key that can never again contribute to quorum, permanently and silently bricking the
            // store. TryLoadTrustedKey is the exact admission check LoadTrustedKeys itself now calls
            // (see AdvisoryVerifier.cs), so the two can never drift apart.
            bool admitted;
            PgpPublicKeyRing? matchedRing;
            try
            {
                admitted = AdvisoryVerifier.TryLoadTrustedKey(fingerprint, armored, out matchedRing);
            }
            catch (Exception e)
            {
                error = $"Rotation contained a public key that could not be checked for admission " +
                        $"({fingerprint}): {e.Message}";
                return false;
            }

            if (!admitted || matchedRing is null)
            {
                error = $"Rotation contained a public key that AdvisoryVerifier could never load as " +
                        $"trusted ({fingerprint}); refusing rather than admitting a key that can " +
                        "never actually contribute to a future quorum.";
                return false;
            }

            // Deferred from Task 4's review: AdvisoryVerifier.Verify deliberately does not check
            // revocation or expiry, so a revoked or expired signer still counts toward quorum
            // THERE. This screens a key being newly admitted right now against the bytes actually
            // submitted in this rotation - never a retroactive scan of a key already in `working`
            // (see the "already trusted" continue above). Review Minor 2: IsRevoked() is
            // packet-presence only - BouncyCastle does not cryptographically validate the
            // revocation signature it finds, so this cannot detect a revocation the submitter
            // simply omitted from the blob. What it actually buys: it catches an honest/
            // keyserver-sourced revoked blob, and lets an adversary who staples on a bogus
            // revocation packet force this rotation to fail (fail-closed - a false refuse, never a
            // false accept - acceptable). Review Minor 3: all three BouncyCastle calls below share
            // one try/catch, not just the parse above - GetValidSeconds() walks self-certification
            // hashed subpackets on real key material, the same class of parsing that can throw on
            // hostile input.
            var primaryKey = matchedRing.GetPublicKey();
            bool isRevoked;
            DateTime? expiresAt;
            try
            {
                isRevoked = primaryKey.IsRevoked();
                // GetValidSeconds() == 0 means "no expiry", per OpenPGP (RFC 4880) and BouncyCastle's
                // own documented contract on PgpPublicKey.GetValidSeconds().
                var validSeconds = primaryKey.GetValidSeconds();
                expiresAt = validSeconds == 0 ? null : primaryKey.CreationTime.AddSeconds(validSeconds);
            }
            catch (Exception e)
            {
                // Cannot determine revocation/expiry status at all -> fail closed exactly like an
                // unreadable key, rather than silently admitting a key that could not be checked.
                error = $"Could not verify revocation/expiry status of a key in the rotation " +
                        $"({fingerprint}): {e.Message}";
                return false;
            }

            if (isRevoked)
            {
                error = $"Rotation attempted to add an already-revoked key ({fingerprint}); refusing.";
                return false;
            }

            if (expiresAt is not null && expiresAt <= DateTime.UtcNow)
            {
                error = $"Rotation attempted to add an already-expired key ({fingerprint}); refusing.";
                return false;
            }

            working.Add(new TrustedKey
            {
                Fingerprint = fingerprint,
                ArmoredPublicKey = armored,
                Identity = fingerprint
            });
        }

        // Review Minor 5: non-empty is not the property that actually matters - a store holding
        // fewer keys than the quorum threshold can never again reach quorum for any future rotation,
        // which is just as permanently stuck as an empty one (e.g. threshold 2, {A, B} -> remove A
        // -> {B}: not empty, but B alone can never meet a quorum of 2 again). Refuse whenever the
        // result would hold fewer keys than the quorum this very rotation was itself held to.
        var minimumTrustStoreSize = Math.Max(1, quorumThreshold);
        if (working.Count < minimumTrustStoreSize)
        {
            error = working.Count == 0
                ? "Rotation would leave the trust store empty; refusing."
                : $"Rotation would leave the trust store with only {working.Count} key(s), below " +
                  $"the quorum threshold of {minimumTrustStoreSize}; refusing.";
            return false;
        }

        updated = working;
        return true;
    }
}
