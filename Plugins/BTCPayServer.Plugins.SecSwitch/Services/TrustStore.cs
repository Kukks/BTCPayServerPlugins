using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

            if (working.Any(k => string.Equals(k.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
                continue; // Already trusted - idempotent no-op, and deliberately not re-screened
                          // below: retroactively scanning an already-trusted key for revocation is
                          // a separate, out-of-scope concern (see the comment on the checks below).

            // Deferred from Task 4's review: AdvisoryVerifier.Verify deliberately does not check
            // revocation or expiry, so a revoked or expired signer still counts toward quorum
            // THERE. This is the right layer to close that specific gap, but only for a key being
            // newly admitted right now - never a retroactive scan of a key already in `working`
            // (see the "already trusted" continue above).
            PgpPublicKey primaryKey;
            try
            {
                primaryKey = ReadPrimaryKey(armored);
            }
            catch (Exception e)
            {
                // Cannot determine revocation/expiry status at all -> fail closed exactly like an
                // unreadable key, rather than silently admitting a key that could not be checked.
                error = $"Could not verify revocation/expiry status of a key in the rotation " +
                        $"({fingerprint}): {e.Message}";
                return false;
            }

            if (primaryKey.IsRevoked())
            {
                error = $"Rotation attempted to add an already-revoked key ({fingerprint}); refusing.";
                return false;
            }

            // GetValidSeconds() == 0 means "no expiry", per OpenPGP (RFC 4880) and BouncyCastle's
            // own documented contract on PgpPublicKey.GetValidSeconds().
            var validSeconds = primaryKey.GetValidSeconds();
            if (validSeconds != 0 && primaryKey.CreationTime.AddSeconds(validSeconds) <= DateTime.UtcNow)
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

        if (working.Count == 0)
        {
            error = "Rotation would leave the trust store empty; refusing.";
            return false;
        }

        updated = working;
        return true;
    }

    /// <summary>
    /// Parses the same "first ring's primary key" that <see cref="AdvisoryVerifier.FingerprintOf"/>
    /// derives a fingerprint from. Re-implemented here rather than called, because that helper's
    /// own parsing internals (<c>ReadPublicKeyRing</c>/<c>PrimaryKeyOf</c>) are private to
    /// <see cref="AdvisoryVerifier"/>. Used only to obtain a <see cref="PgpPublicKey"/> object to
    /// run the revocation/expiry checks above against - never used to derive the fingerprint that
    /// gets stored on a <see cref="TrustedKey"/>, which always comes from a direct call to
    /// <see cref="AdvisoryVerifier.FingerprintOf"/> instead (see the CRITICAL CONTRACT comment
    /// above).
    /// </summary>
    static PgpPublicKey ReadPrimaryKey(string armoredPublicKey)
    {
        using var input = PgpUtilities.GetDecoderStream(
            new MemoryStream(Encoding.ASCII.GetBytes(armoredPublicKey)));
        var bundle = new PgpPublicKeyRingBundle(input);

        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
            return ring.GetPublicKey();

        throw new InvalidDataException("No usable public key ring found in armored block.");
    }
}
