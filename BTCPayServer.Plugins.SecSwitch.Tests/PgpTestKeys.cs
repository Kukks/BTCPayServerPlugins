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
}
