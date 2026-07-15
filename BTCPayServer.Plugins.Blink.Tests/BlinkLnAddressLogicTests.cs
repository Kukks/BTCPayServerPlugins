using System;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blink;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using Xunit;

// Tests for the non-custodial (Spark / LNURL) client's pure logic: LUD-21 preimage validation,
// invoice status decision, back-off math, lightning-address parsing, LNURL metadata URL building,
// verify-URL construction, origin extraction, and amount-bounds checks.
public class BlinkLnAddressLogicTests
{
    // A real Blink transaction fixture (from BlinkLightningClient.cs comments): this preimage's
    // SHA256 equals this payment hash (verified).
    private const string ValidPreimage = "9d726f2530323bc54c8540481ff9ef20fca22f9da9c39b883a81449aa09ecedd";
    private const string ValidPaymentHash = "de3073bc40acbbf2948259b7c56212c1e23f030cd113b489aa4805ba46a772bb";

    // ---- Preimage validation ----

    [Fact]
    public void ValidatePreimage_accepts_matching_preimage()
    {
        Assert.Equal(ValidPreimage, BlinkLnAddressLightningClient.ValidatePreimage(ValidPaymentHash, ValidPreimage));
    }

    [Fact]
    public void ValidatePreimage_is_case_insensitive_on_hash()
    {
        Assert.Equal(ValidPreimage,
            BlinkLnAddressLightningClient.ValidatePreimage(ValidPaymentHash.ToUpperInvariant(), ValidPreimage));
    }

    [Fact]
    public void ValidatePreimage_rejects_wrong_preimage()
    {
        var wrong = new string('a', 64);
        Assert.Null(BlinkLnAddressLightningClient.ValidatePreimage(ValidPaymentHash, wrong));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tooshort")]
    [InlineData("zzzz6f2530323bc54c8540481ff9ef20fca22f9da9c39b883a81449aa09ecedd")] // 64 chars but non-hex
    public void ValidatePreimage_rejects_malformed(string? preimage)
    {
        Assert.Null(BlinkLnAddressLightningClient.ValidatePreimage(ValidPaymentHash, preimage));
    }

    [Fact]
    public void ValidatePreimage_rejects_when_payment_hash_not_64_hex()
    {
        Assert.Null(BlinkLnAddressLightningClient.ValidatePreimage("nothex", ValidPreimage));
    }

    [Fact]
    public void ValidatePreimage_roundtrips_a_generated_pair()
    {
        var preimageBytes = RandomUtils.GetBytes(32);
        var preimage = Encoders.Hex.EncodeData(preimageBytes);
        var paymentHash = Encoders.Hex.EncodeData(Hashes.SHA256(preimageBytes));
        Assert.Equal(preimage, BlinkLnAddressLightningClient.ValidatePreimage(paymentHash, preimage));
    }

    // ---- IsHex ----

    [Theory]
    [InlineData("deadbeef", true)]
    [InlineData("DEADBEEF", true)]
    [InlineData("0123456789abcdefABCDEF", true)]
    [InlineData("xyz", false)]
    [InlineData("dead beef", false)]
    public void IsHex_detects_hex(string input, bool expected)
    {
        Assert.Equal(expected, BlinkLnAddressLightningClient.IsHex(input));
    }

    // ---- Invoice status decision ----

    [Fact]
    public void DetermineStatus_paid_when_settled()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(LightningInvoiceStatus.Paid,
            BlinkLnAddressLightningClient.DetermineStatus(true, now.AddMinutes(-10), now));
    }

    [Fact]
    public void DetermineStatus_expired_when_unsettled_and_past_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(LightningInvoiceStatus.Expired,
            BlinkLnAddressLightningClient.DetermineStatus(false, now.AddSeconds(-1), now));
    }

    [Fact]
    public void DetermineStatus_unpaid_when_unsettled_and_not_expired()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(LightningInvoiceStatus.Unpaid,
            BlinkLnAddressLightningClient.DetermineStatus(false, now.AddMinutes(10), now));
    }

    [Fact]
    public void DetermineStatus_unpaid_when_expiry_equals_now()
    {
        // Expiry is strictly-less-than (expiresAt < now => Expired), so an invoice whose expiry equals
        // 'now' is still Unpaid. Pins this intentional tie-break to avoid a 1-tick premature expiry.
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(LightningInvoiceStatus.Unpaid,
            BlinkLnAddressLightningClient.DetermineStatus(false, now, now));
    }

    // ---- Back-off math ----

    [Fact]
    public void NextBackoff_grows_exponentially_from_poll_interval()
    {
        var poll = TimeSpan.FromSeconds(3);
        var max = TimeSpan.FromSeconds(30);

        var (e1, d1) = BlinkLnAddressLightningClient.NextBackoff(0, poll, max);
        Assert.Equal(1, e1);
        Assert.Equal(TimeSpan.FromSeconds(6), d1); // 3 * 2^1

        var (e2, d2) = BlinkLnAddressLightningClient.NextBackoff(1, poll, max);
        Assert.Equal(2, e2);
        Assert.Equal(TimeSpan.FromSeconds(12), d2); // 3 * 2^2
    }

    [Fact]
    public void NextBackoff_caps_at_max()
    {
        var poll = TimeSpan.FromSeconds(3);
        var max = TimeSpan.FromSeconds(30);
        var (errors, delay) = BlinkLnAddressLightningClient.NextBackoff(10, poll, max);
        Assert.Equal(11, errors);
        Assert.Equal(max, delay);
    }

    // ---- Lightning-address parsing ----

    [Fact]
    public void ParseLightningAddress_splits_user_and_domain()
    {
        var (user, domain) = BlinkLnAddressLightningClient.ParseLightningAddress("twentyone@blink.sv");
        Assert.Equal("twentyone", user);
        Assert.Equal("blink.sv", domain);
    }

    [Theory]
    [InlineData("noatsign")]
    [InlineData("@blink.sv")]
    [InlineData("user@")]
    [InlineData("a@b@c")]
    [InlineData("")]
    public void ParseLightningAddress_throws_on_invalid(string address)
    {
        Assert.Throws<FormatException>(() => BlinkLnAddressLightningClient.ParseLightningAddress(address));
    }

    // ---- LNURL metadata URL ----

    [Fact]
    public void BuildLnurlpMetadataUri_builds_https_for_normal_domain()
    {
        var uri = BlinkLnAddressLightningClient.BuildLnurlpMetadataUri("twentyone", "blink.sv", usd: false);
        Assert.Equal("https://blink.sv/.well-known/lnurlp/twentyone", uri.ToString());
    }

    [Fact]
    public void BuildLnurlpMetadataUri_appends_usd_modifier()
    {
        var uri = BlinkLnAddressLightningClient.BuildLnurlpMetadataUri("twentyone", "blink.sv", usd: true);
        // '+' is URL-escaped to %2B in the path segment.
        Assert.Equal("https://blink.sv/.well-known/lnurlp/twentyone%2Busd", uri.ToString());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8080")]
    public void BuildLnurlpMetadataUri_uses_http_for_localhost(string domain)
    {
        var uri = BlinkLnAddressLightningClient.BuildLnurlpMetadataUri("u", domain, usd: false);
        Assert.Equal("http", uri.Scheme);
    }

    // ---- Verify URL + origin ----

    [Fact]
    public void BuildVerifyUrl_composes_origin_and_hash()
    {
        Assert.Equal("https://lnurl.blink.sv/verify/abcd",
            BlinkLnAddressLightningClient.BuildVerifyUrl("https://lnurl.blink.sv", "abcd"));
    }

    [Fact]
    public void ExtractOrigin_returns_scheme_and_authority()
    {
        Assert.Equal("https://lnurl.blink.sv",
            BlinkLnAddressLightningClient.ExtractOrigin("https://lnurl.blink.sv/lnurlp/blink.sv/twentyone/invoice"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    // Non-http(s) absolute URLs must be rejected by the scheme guard (they would otherwise pass the
    // Uri.TryCreate gate). This locks in the http/https-only behavior of ExtractOrigin.
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ws://lnurl.blink.sv/lnurlp/foo")]
    public void ExtractOrigin_returns_null_for_invalid(string? url)
    {
        Assert.Null(BlinkLnAddressLightningClient.ExtractOrigin(url));
    }

    // ---- Amount bounds ----

    [Fact]
    public void ValidateAmountBounds_passes_within_range()
    {
        BlinkLnAddressLightningClient.ValidateAmountBounds(210_000, 1_000, 1_000_000);
    }

    [Fact]
    public void ValidateAmountBounds_passes_at_exact_min()
    {
        // The bound is exclusive-below (amountMsat < min throws), so amount == min is accepted.
        BlinkLnAddressLightningClient.ValidateAmountBounds(1_000, 1_000, 1_000_000);
    }

    [Fact]
    public void ValidateAmountBounds_passes_at_exact_max()
    {
        // The bound is exclusive-above (amountMsat > max throws), so amount == max is accepted.
        BlinkLnAddressLightningClient.ValidateAmountBounds(1_000_000, 1_000, 1_000_000);
    }

    [Fact]
    public void ValidateAmountBounds_throws_below_min()
    {
        var ex = Assert.Throws<Exception>(() => BlinkLnAddressLightningClient.ValidateAmountBounds(500, 1_000, 1_000_000));
        Assert.Contains("below", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAmountBounds_throws_above_max()
    {
        var ex = Assert.Throws<Exception>(() => BlinkLnAddressLightningClient.ValidateAmountBounds(2_000_000, 1_000, 1_000_000));
        Assert.Contains("above", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
