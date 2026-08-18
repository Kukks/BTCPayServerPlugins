using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blink;
using LNURL;
using Newtonsoft.Json.Linq;
using Xunit;

// Tests for the LNURL-pay alignment logic that lets non-custodial (Blink ln-address) stores serve
// metadata/bounds matching the Blink-minted invoice, so strict (LUD-06) wallets accept the payment.
public class BlinkLnurlRequestFilterTests
{
    // --- TryGetBlinkLnAddress: connection-string detection -------------------------------------

    [Fact]
    public void Detects_blink_ln_address_connection_string()
    {
        var ok = BlinkLnurlRequestFilter.TryGetBlinkLnAddress(
            "type=blink;ln-address=twentyone@blink.sv", out var lnAddress, out var usd);
        Assert.True(ok);
        Assert.Equal("twentyone@blink.sv", lnAddress);
        Assert.False(usd);
    }

    [Fact]
    public void Bare_username_defaults_to_blink_sv_domain()
    {
        var ok = BlinkLnurlRequestFilter.TryGetBlinkLnAddress(
            "type=blink;ln-address=twentyone", out var lnAddress, out _);
        Assert.True(ok);
        Assert.Equal("twentyone@blink.sv", lnAddress);
    }

    [Fact]
    public void Usd_currency_sets_usd_flag()
    {
        var ok = BlinkLnurlRequestFilter.TryGetBlinkLnAddress(
            "type=blink;ln-address=twentyone@blink.sv;currency=USD", out _, out var usd);
        Assert.True(ok);
        Assert.True(usd);
    }

    [Fact]
    public void Custodial_api_key_is_not_ln_address()
    {
        var ok = BlinkLnurlRequestFilter.TryGetBlinkLnAddress(
            "type=blink;api-key=blink_abc;server=https://api.blink.sv/graphql", out var lnAddress, out _);
        Assert.False(ok);
        Assert.Null(lnAddress);
    }

    [Fact]
    public void Non_blink_connection_string_is_ignored()
    {
        var ok = BlinkLnurlRequestFilter.TryGetBlinkLnAddress(
            "type=lnd-rest;server=https://mynode.example.com", out _, out _);
        Assert.False(ok);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a connection string")]
    public void Invalid_connection_strings_do_not_throw(string connectionString)
    {
        Assert.False(BlinkLnurlRequestFilter.TryGetBlinkLnAddress(connectionString, out _, out _));
    }

    // --- ApplyBlinkParameters: metadata mirroring + bounds intersection ------------------------

    private static JObject BlinkMeta(string metadata = "[[\"text/plain\",\"Pay to twentyone@blink.sv\"]]",
        long minSendable = 1000, long maxSendable = 50000000000, int commentAllowed = 255)
        => new()
        {
            ["metadata"] = metadata,
            ["minSendable"] = minSendable,
            ["maxSendable"] = maxSendable,
            ["commentAllowed"] = commentAllowed
        };

    [Fact]
    public void Mirrors_blink_metadata_so_description_hash_matches()
    {
        var req = new LNURLPayRequest { Metadata = "[[\"text/plain\",\"BTCPay store\"]]" };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta());
        Assert.Equal("[[\"text/plain\",\"Pay to twentyone@blink.sv\"]]", req.Metadata);
    }

    [Fact]
    public void Topup_bounds_are_narrowed_to_blink_limits()
    {
        // BTCPay top-up defaults: 1 sat .. 6.12 BTC. Blink: 1 sat .. 0.5 BTC.
        var req = new LNURLPayRequest
        {
            MinSendable = LightMoney.Satoshis(1),
            MaxSendable = LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC)
        };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta());
        Assert.Equal(LightMoney.MilliSatoshis(1000), req.MinSendable);
        Assert.Equal(LightMoney.MilliSatoshis(50000000000), req.MaxSendable);
    }

    [Fact]
    public void Fixed_amount_invoice_bounds_are_preserved_when_within_blink_range()
    {
        // A fixed-amount invoice has min == max; it must stay exact after alignment.
        var fixedAmt = LightMoney.Satoshis(21000);
        var req = new LNURLPayRequest { MinSendable = fixedAmt, MaxSendable = fixedAmt };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta());
        Assert.Equal(fixedAmt, req.MinSendable);
        Assert.Equal(fixedAmt, req.MaxSendable);
    }

    [Fact]
    public void Bounds_are_never_widened_beyond_btcpay_request()
    {
        // Blink advertises a wider range than BTCPay; we must not widen BTCPay's own bounds.
        var req = new LNURLPayRequest
        {
            MinSendable = LightMoney.Satoshis(10),
            MaxSendable = LightMoney.Satoshis(1000)
        };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta(minSendable: 1000, maxSendable: 50000000000));
        Assert.Equal(LightMoney.Satoshis(10), req.MinSendable);   // not lowered to 1 sat
        Assert.Equal(LightMoney.Satoshis(1000), req.MaxSendable); // not raised to 0.5 BTC
    }

    [Fact]
    public void Disjoint_ranges_leave_btcpay_bounds_untouched()
    {
        // Degenerate: Blink's minimum (10 sat) exceeds BTCPay's maximum (5 sat) - the intersection is
        // empty. We must NOT fabricate a fixed amount Blink would reject; leave BTCPay's bounds as-is
        // (the callback's own amount-bounds check rejects out-of-range amounts cleanly).
        var req = new LNURLPayRequest
        {
            MinSendable = LightMoney.Satoshis(1),
            MaxSendable = LightMoney.Satoshis(5)
        };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta(minSendable: 10000 /*10 sat*/, maxSendable: 50000000000));
        Assert.Equal(LightMoney.Satoshis(1), req.MinSendable);
        Assert.Equal(LightMoney.Satoshis(5), req.MaxSendable);
    }

    [Fact]
    public void Comment_allowed_is_capped_to_blink_limit()
    {
        var req = new LNURLPayRequest { CommentAllowed = 2000 };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta(commentAllowed: 255));
        Assert.Equal(255, req.CommentAllowed);
    }

    [Fact]
    public void Comment_allowed_not_raised_when_btcpay_lower()
    {
        var req = new LNURLPayRequest { CommentAllowed = 100 };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta(commentAllowed: 255));
        Assert.Equal(100, req.CommentAllowed);
    }

    // --- custodial mode: metadata must NOT be mirrored ---------------------------------------

    [Fact]
    public void Custodial_mode_does_not_replace_metadata_but_still_narrows_bounds()
    {
        // For custodial accounts the invoice commits to BTCPay's own metadata, so mirroring must be
        // skipped (mirrorMetadata=false) - otherwise the description hash mismatches AND a
        // text/identifier leaks that makes the Blink app pay intraledger, bypassing the invoice.
        var original = "[[\"text/plain\",\"BTCPay store\"]]";
        var req = new LNURLPayRequest
        {
            Metadata = original,
            MinSendable = LightMoney.Satoshis(1),
            MaxSendable = LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC)
        };
        BlinkLnurlRequestFilter.ApplyBlinkParameters(req, BlinkMeta(), mirrorMetadata: false);
        // metadata untouched...
        Assert.Equal(original, req.Metadata);
        // ...but bounds still narrowed to Blink's limits.
        Assert.Equal(LightMoney.MilliSatoshis(50000000000), req.MaxSendable);
    }
}
