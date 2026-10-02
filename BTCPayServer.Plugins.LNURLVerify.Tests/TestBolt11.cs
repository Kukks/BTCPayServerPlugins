using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

/// <summary>Mints correctly signed regtest BOLT11 invoices for a chosen payment hash (BOLT #11 encoding).</summary>
public static class TestBolt11
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public static string Create(Key nodeKey, long amountMsat, byte[] paymentHash, string description = "lnurlverify test",
        int expirySeconds = 3600)
    {
        if (amountMsat % 100 != 0) throw new ArgumentException("must be a multiple of 100 msat (the n unit)", nameof(amountMsat));
        var hrp = $"lnbcrt{amountMsat / 100}n";
        var words = new List<byte>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 6; i >= 0; i--) words.Add((byte)((timestamp >> (5 * i)) & 31));
        Field(words, 1, ToWords(paymentHash));
        Field(words, 16, ToWords(RandomNumberGenerator.GetBytes(32)));
        Field(words, 13, ToWords(Encoding.UTF8.GetBytes(description)));
        Field(words, 6, IntWords(expirySeconds));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(hrp).Concat(FromWords(words)).ToArray());
        var signature = nodeKey.SignCompact(new uint256(digest), false);
        words.AddRange(ToWords(signature.Signature.Append((byte)signature.RecoveryId).ToArray()));
        return Bech32(hrp, words);
    }

    private static void Field(List<byte> words, byte type, List<byte> data)
    {
        words.Add(type);
        words.Add((byte)(data.Count >> 5));
        words.Add((byte)(data.Count & 31));
        words.AddRange(data);
    }

    private static List<byte> IntWords(long value)
    {
        var words = new List<byte>();
        do { words.Insert(0, (byte)(value & 31)); value >>= 5; } while (value > 0);
        return words;
    }

    private static List<byte> ToWords(byte[] data)
    {
        int acc = 0, bits = 0;
        var words = new List<byte>();
        foreach (var b in data)
        {
            acc = ((acc << 8) | b) & 0xFFF;
            bits += 8;
            while (bits >= 5) { bits -= 5; words.Add((byte)((acc >> bits) & 31)); }
        }
        if (bits > 0) words.Add((byte)((acc << (5 - bits)) & 31));
        return words;
    }

    private static byte[] FromWords(List<byte> words)
    {
        int acc = 0, bits = 0;
        var bytes = new List<byte>();
        foreach (var w in words)
        {
            acc = ((acc << 5) | w) & 0xFFF;
            bits += 5;
            while (bits >= 8) { bits -= 8; bytes.Add((byte)((acc >> bits) & 0xFF)); }
        }
        if (bits > 0) bytes.Add((byte)((acc << (8 - bits)) & 0xFF));
        return bytes.ToArray();
    }

    private static string Bech32(string hrp, List<byte> data)
    {
        var values = hrp.Select(c => (byte)(c >> 5)).Append((byte)0).Concat(hrp.Select(c => (byte)(c & 31)))
            .Concat(data).Concat(new byte[6]);
        var mod = Polymod(values) ^ 1;
        var checksum = Enumerable.Range(0, 6).Select(i => (byte)((mod >> (5 * (5 - i))) & 31));
        return hrp + "1" + new string(data.Concat(checksum).Select(d => Charset[d]).ToArray());
    }

    private static uint Polymod(IEnumerable<byte> values)
    {
        uint[] generator = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) != 0) chk ^= generator[i];
        }
        return chk;
    }
}
