using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// Test-only PGP key generation and detached signing.
public sealed class PgpTestKey
{
    public required string ArmoredPublicKey { get; init; }
    public required PgpSecretKey SecretKey { get; init; }
    public required string Fingerprint { get; init; }

    public string SignDetached(byte[] payload)
    {
        var privateKey = SecretKey.ExtractPrivateKey(Array.Empty<char>());
        var gen = new PgpSignatureGenerator(SecretKey.PublicKey.Algorithm, HashAlgorithmTag.Sha256);
        gen.InitSign(PgpSignature.BinaryDocument, privateKey);
        gen.Update(payload);
        var signature = gen.Generate();

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        using (var bcpg = new BcpgOutputStream(armored))
        {
            signature.Encode(bcpg);
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }
}

public static class PgpTestKeys
{
    public static PgpTestKey Generate(string identity)
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new RsaKeyGenerationParameters(
            BigInteger.ValueOf(0x10001), new SecureRandom(), strength: 2048, certainty: 25));
        var keyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, gen.GenerateKeyPair(), DateTime.UtcNow);

        var secretKey = new PgpSecretKey(
            PgpSignature.DefaultCertification,
            keyPair,
            identity,
            SymmetricKeyAlgorithmTag.Null,
            Array.Empty<char>(),
            useSha1: true,
            hashedPackets: null,
            unhashedPackets: null,
            rand: new SecureRandom());

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            secretKey.PublicKey.Encode(armored);
        }

        return new PgpTestKey
        {
            ArmoredPublicKey = Encoding.ASCII.GetString(output.ToArray()),
            SecretKey = secretKey,
            Fingerprint = Convert.ToHexString(secretKey.PublicKey.GetFingerprint())
        };
    }

    /// <summary>
    /// Generates a real master key plus a signing subkey, in one ring - the hardened layout an
    /// offline-primary/online-subkey OpenPGP setup (and <c>gpg --detach-sign</c>) produces. The
    /// returned <see cref="PgpTestKey"/>'s <c>SecretKey</c> is the SUBKEY's (so
    /// <see cref="PgpTestKey.SignDetached"/> signs with the subkey, as a real deployment would),
    /// while <c>Fingerprint</c> and <c>ArmoredPublicKey</c> both describe the whole ring, keyed by
    /// the ring's primary/master identity.
    /// </summary>
    public static PgpTestKey GenerateWithSigningSubkey(string identity)
    {
        var masterGen = new RsaKeyPairGenerator();
        masterGen.Init(new RsaKeyGenerationParameters(
            BigInteger.ValueOf(0x10001), new SecureRandom(), strength: 2048, certainty: 25));
        var masterKeyPair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral, masterGen.GenerateKeyPair(), DateTime.UtcNow);

        var subGen = new RsaKeyPairGenerator();
        subGen.Init(new RsaKeyGenerationParameters(
            BigInteger.ValueOf(0x10001), new SecureRandom(), strength: 2048, certainty: 25));
        var subKeyPair = new PgpKeyPair(
            PublicKeyAlgorithmTag.RsaGeneral, subGen.GenerateKeyPair(), DateTime.UtcNow);

        var ringGen = new PgpKeyRingGenerator(
            PgpSignature.DefaultCertification,
            masterKeyPair,
            identity,
            SymmetricKeyAlgorithmTag.Null,
            Array.Empty<char>(),
            useSha1: true,
            hashedPackets: null,
            unhashedPackets: null,
            rand: new SecureRandom());
        ringGen.AddSubKey(subKeyPair);

        var publicRing = ringGen.GeneratePublicKeyRing();
        var secretRing = ringGen.GenerateSecretKeyRing();

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            publicRing.Encode(armored);
        }

        PgpSecretKey? subkeySecret = null;
        foreach (PgpSecretKey key in secretRing.GetSecretKeys())
        {
            if (key.PublicKey.KeyId == subKeyPair.KeyId)
            {
                subkeySecret = key;
                break;
            }
        }
        if (subkeySecret is null)
            throw new InvalidOperationException("Generated secret key ring did not contain the expected subkey.");

        return new PgpTestKey
        {
            ArmoredPublicKey = Encoding.ASCII.GetString(output.ToArray()),
            SecretKey = subkeySecret,
            Fingerprint = Convert.ToHexString(publicRing.GetPublicKey().GetFingerprint())
        };
    }

    /// <summary>
    /// Concatenates the raw (de-armored) packet bytes of each detached signature and re-armors
    /// them as a single block - what a multi-signature <c>.asc</c> file (e.g. from signing twice
    /// with <c>gpg --detach-sign</c>, or from prepending a second signature) actually looks like on
    /// the wire: one armor wrapper around several back-to-back signature packets.
    /// </summary>
    public static string CombineArmoredSignatures(params string[] armoredSignatures)
        => CombineArmoredBlocks(armoredSignatures);

    /// <summary>
    /// Concatenates the raw (de-armored) packet bytes of each armored public key/ring and re-armors
    /// them as a single blob - what pasting several keys into one "trusted keys" text box actually
    /// looks like on the wire: one armor wrapper around several back-to-back key rings. Also used to
    /// simulate an attacker stapling an entire second (rogue) ring onto a victim's blob, since a
    /// bare "Public Key"-tagged packet always starts a new ring rather than extending the one before
    /// it - unlike a "Public Subkey"-tagged packet (see <see cref="StapleRogueSubkey"/>).
    /// </summary>
    public static string CombineArmoredPublicKeys(params string[] armoredPublicKeys)
        => CombineArmoredBlocks(armoredPublicKeys);

    static string CombineArmoredBlocks(params string[] armoredBlocks)
    {
        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            foreach (var block in armoredBlocks)
            {
                var decoded = Dearmor(block);
                armored.Write(decoded, 0, decoded.Length);
            }
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }

    /// <summary>
    /// Simulates an attacker stapling a rogue key onto a victim's otherwise-untouched armored
    /// public key blob - poisoning a blob an admin might import as trusted (a compromised keyserver
    /// mirror, a malicious "add a trusted key" PR, a MITM'd fetch). The rogue key is the real
    /// "Public Subkey"-tagged packet extracted from <paramref name="rogueRing"/> (build with
    /// <see cref="GenerateWithSigningSubkey"/>) - not a hand-crafted one - so it parses as part of
    /// the victim's ring instead of starting a second one.
    ///
    /// <paramref name="includeBindingSignature"/> controls exactly how many subkey-binding
    /// signatures the re-parsed rogue subkey carries: <c>false</c> means genuinely zero,
    /// <c>true</c> means exactly one, real, produced by <paramref name="rogueRing"/>'s OWN master
    /// key - never the victim's. Getting <c>false</c> to mean zero takes an extra step:
    /// <see cref="PgpPublicKey.Encode(Stream)"/> also writes out a key's own attached signatures, so
    /// encoding the subkey as extracted (which still carries its self-produced binding signature)
    /// would emit one regardless of this parameter. <see cref="PgpPublicKey.RemoveCertification(PgpPublicKey,PgpSignature)"/>
    /// (BouncyCastle's own public API for this, confirmed by reflection and an end-to-end
    /// encode/re-parse round trip) strips it first, so the <c>true</c> case then re-adds exactly one
    /// copy explicitly rather than relying on - and duplicating - the auto-embedded one.
    ///
    /// Either way the signature (when present) verifies only against <paramref name="rogueRing"/>'s
    /// own master, never the victim's, so both cases prove "a binding signature is not enough - it
    /// must verify against the specific primary it's stapled onto" and "no binding signature at all
    /// is rejected identically". <paramref name="rogueRing"/> keeps its own usable secret key for
    /// the rogue subkey, so a test can sign with it directly and confirm the forged signature still
    /// does not verify against the victim's (now poisoned) trusted entry.
    /// </summary>
    public static string StapleRogueSubkey(PgpTestKey victim, PgpTestKey rogueRing, bool includeBindingSignature)
    {
        using var rogueInput = PgpUtilities.GetDecoderStream(
            new MemoryStream(Encoding.ASCII.GetBytes(rogueRing.ArmoredPublicKey)));
        var rogueBundle = new PgpPublicKeyRingBundle(rogueInput);

        PgpPublicKey? rogueSubkey = null;
        foreach (PgpPublicKeyRing ring in rogueBundle.GetKeyRings())
        {
            foreach (PgpPublicKey key in ring.GetPublicKeys())
            {
                if (key.IsMasterKey) continue;
                rogueSubkey = key;
                break;
            }
            if (rogueSubkey is not null) break;
        }
        if (rogueSubkey is null)
            throw new InvalidOperationException("rogueRing did not contain a subkey for this test helper.");

        PgpSignature? bindingSignature = null;
        foreach (PgpSignature sig in rogueSubkey.GetSignaturesOfType(PgpSignature.SubkeyBinding))
        {
            bindingSignature = sig;
            break;
        }
        if (bindingSignature is null)
            throw new InvalidOperationException("The rogue subkey did not carry a binding signature to steal.");

        // Strip the subkey's own embedded binding signature before encoding it, so encoding never
        // auto-emits one regardless of includeBindingSignature - see the doc comment above.
        var bareSubkey = PgpPublicKey.RemoveCertification(rogueSubkey, bindingSignature);

        var victimBytes = Dearmor(victim.ArmoredPublicKey);

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            armored.Write(victimBytes, 0, victimBytes.Length);
            bareSubkey.Encode(armored);

            if (includeBindingSignature)
                bindingSignature.Encode(armored);
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }

    static byte[] Dearmor(string armored)
    {
        using var decoded = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.ASCII.GetBytes(armored)));
        using var buffer = new MemoryStream();
        decoded.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Test-only packet surgery: rewrites the 8-byte issuer key id embedded in a detached
    /// signature's bytes from <paramref name="fromKeyId"/> to <paramref name="toKeyId"/>, without
    /// touching anything else, then re-armors. This simulates a hostile relay tampering with the
    /// *unhashed* Issuer Key ID subpacket of a v4 signature - legal because that subpacket is not
    /// covered by the signature's own hash, so rewriting it invalidates nothing cryptographically.
    /// Throws if the source key id's bytes do not appear in the encoded signature exactly once,
    /// rather than silently editing the wrong bytes or no-op'ing.
    /// </summary>
    public static string RewriteIssuerKeyId(string armoredSignature, long fromKeyId, long toKeyId)
    {
        var raw = Dearmor(armoredSignature);

        var needle = KeyIdBytes(fromKeyId);
        var replacement = KeyIdBytes(toKeyId);

        var matchOffset = -1;
        var matchCount = 0;
        for (var i = 0; i <= raw.Length - needle.Length; i++)
        {
            var isMatch = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (raw[i + j] != needle[j])
                {
                    isMatch = false;
                    break;
                }
            }
            if (!isMatch) continue;
            matchCount++;
            matchOffset = i;
        }

        if (matchCount != 1)
            throw new InvalidOperationException(
                $"Expected exactly one occurrence of the source key id in the encoded signature, " +
                $"found {matchCount}. Packet-level surgery is not safe to apply blindly.");

        Array.Copy(replacement, 0, raw, matchOffset, replacement.Length);

        using var output = new MemoryStream();
        using (var armored = new ArmoredOutputStream(output))
        {
            armored.Write(raw, 0, raw.Length);
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }

    static byte[] KeyIdBytes(long keyId)
    {
        // OpenPGP stores all multi-byte scalar values big-endian (network byte order), regardless
        // of host machine endianness.
        var bytes = new byte[8];
        for (var i = 0; i < 8; i++)
            bytes[i] = (byte)(keyId >> (8 * (7 - i)));
        return bytes;
    }
}
