using System;
using System.Security.Cryptography;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TestBolt11Tests
{
    [Fact]
    public void Minted_invoices_parse_verify_and_keep_the_payment_hash_byte_order()
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);

        var parsed = BOLT11PaymentRequest.Parse(TestBolt11.Create(new Key(), 1_234_000, hash), Network.RegTest);

        Assert.True(parsed.VerifySignature());
        Assert.Equal(LightMoney.MilliSatoshis(1_234_000), parsed.MinimumAmount);
        // The plugin checks SHA256(preimage) against PaymentHash.ToString(): this pins that byte order.
        Assert.Equal(Convert.ToHexString(hash).ToLowerInvariant(), parsed.PaymentHash!.ToString());
        Assert.True(LNURLReceiver.IsValidPreimage(Convert.ToHexString(preimage), parsed.PaymentHash.ToString()));
    }
}
