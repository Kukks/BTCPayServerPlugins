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

    [Fact]
    public void Advertised_units_this_server_does_not_offer_are_listed()
    {
        var pay = Newtonsoft.Json.Linq.JObject.Parse(
            "{\"units\":[{\"code\":\"USDT\",\"decimals\":6},{\"code\":\"EURC\",\"decimals\":6},{\"code\":\"PYUSD\",\"decimals\":6}],\"paymentOptions\":[" +
            "{\"id\":\"a\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\"}," +
            "{\"id\":\"b\",\"type\":\"eip155\",\"asset\":\"eip155:8453/erc20:0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42\",\"unit\":\"EURC\"}," +
            "{\"id\":\"c\",\"type\":\"eip155\",\"asset\":\"eip155:1/erc20:0x6c3ea9036406852006290770BEdFcAbA0e23A0e8\",\"unit\":\"PYUSD\",\"verifiable\":false}]}");
        Assert.Equal(new[] { "EURC" }, TokenAssets.Parse("USDT,USDC").Assets.Unconfigured(pay));
    }
}
