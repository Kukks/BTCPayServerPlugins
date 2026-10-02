using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TokenAssetsTests
{
    [Fact]
    public void The_default_offers_usdt_and_usdc()
    {
        var (assets, rejected) = TokenAssets.Parse(null);
        Assert.Equal(new[] { "USDT", "USDC" }, assets.Codes);
        Assert.Equal(new[] { "LNURL-USDT", "LNURL-USDC" }, assets.PaymentMethodIds.Select(p => p.ToString()));
        Assert.Empty(rejected);
    }

    [Fact]
    public void Codes_are_trimmed_upper_cased_and_deduplicated() =>
        Assert.Equal(new[] { "USDT", "EURC" }, TokenAssets.Parse(" usdt, EURC ,USDT,,").Assets.Codes);

    [Fact]
    public void An_empty_setting_offers_no_tokens() => Assert.Empty(TokenAssets.Parse("").Assets.Codes);

    [Fact]
    public void Codes_that_would_collide_or_break_a_payment_method_id_are_rejected()
    {
        var (assets, rejected) = TokenAssets.Parse("USDT,ARKADE,onchain,US-DT,ABCDEFGHIJKLMNOPQ");
        Assert.Equal(new[] { "USDT" }, assets.Codes);
        Assert.Equal(new[] { "ARKADE", "onchain", "US-DT", "ABCDEFGHIJKLMNOPQ" }, rejected);
    }
}
