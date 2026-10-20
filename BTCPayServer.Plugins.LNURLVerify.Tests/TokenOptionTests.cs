using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TokenOptionTests
{
    const string Arbitrum = "{\"id\":\"usdt-arbitrum\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\"}";
    const string SameId = "{\"id\":\"usdt-arbitrum\",\"type\":\"eip155\",\"asset\":\"eip155:8453/erc20:0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913\",\"unit\":\"USDT\"}";
    const string UpperId = "{\"id\":\"USDT-ARBITRUM\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\"}";

    static JObject Pay(string options, string units = "[{\"code\":\"USDT\",\"decimals\":6,\"name\":\"Tether USD\"}]") =>
        JObject.Parse("{\"units\":" + units + ",\"paymentOptions\":[" + options + "]}");

    [Fact]
    public void A_token_option_carries_its_asset_and_unit()
    {
        var o = Assert.Single(TokenOption.Parse(Pay(Arbitrum)));
        Assert.Equal(("usdt-arbitrum", "eip155:42161", "USDT", 6, "Tether USD", true),
            (o.Id, o.Asset.ChainId, o.Unit.Code, o.Unit.Decimals, o.Unit.Name, o.Available));
    }

    [Fact]
    public void A_token_option_carries_its_provider_and_none_when_the_lnurl_names_none()
    {
        var viaFixedFloat = Arbitrum.Replace("usdt-arbitrum", "ff-usdtarbitrum").Replace("}", ",\"provider\":\"FixedFloat\"}");
        Assert.Equal(new[] { null, "FixedFloat" }, TokenOption.Parse(Pay(Arbitrum + "," + viaFixedFloat)).Select(o => o.Provider));
    }

    [Theory]
    [InlineData("{\"id\":\"x\",\"type\":\"solana\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\"}")]
    [InlineData("{\"id\":\"x\",\"type\":\"eip155\",\"asset\":\"eip155:42161\",\"unit\":\"USDT\"}")]
    [InlineData("{\"id\":\"x\",\"type\":\"cosmos\",\"asset\":\"cosmos:cosmoshub-4/slip44:118\",\"unit\":\"USDT\"}")]
    [InlineData("{\"id\":\"x\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\"}")]
    [InlineData("{\"id\":\"x\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDC\"}")]
    [InlineData("{\"id\":\"arkade\",\"type\":\"arkade\"}")]
    public void Options_a_client_cannot_pay_are_ignored(string option) => Assert.Empty(TokenOption.Parse(Pay(option)));

    [Fact]
    public void Units_need_an_alphanumeric_code_and_integer_decimals()
    {
        var units = TokenOption.Units(Pay("", "[{\"code\":\"USDT\",\"decimals\":6},{\"code\":\"US-DT\",\"decimals\":6},{\"code\":\"EURC\",\"decimals\":\"6\"}," +
                                             "{\"code\":\"DAI\",\"decimals\":37},{\"code\":\"usdc\",\"decimals\":6,\"name\":\"\"},{\"code\":\"BIG\",\"decimals\":99999999999999999999}]"));
        Assert.Equal(new[] { "USDC", "USDT" }, units.Values.Select(u => u.Code).OrderBy(c => c));
        Assert.Null(units["USDC"].Name);
    }

    [Fact]
    public void Every_option_sharing_an_id_is_dropped()
    {
        Assert.Empty(TokenOption.Parse(Pay(Arbitrum + "," + SameId)));
        Assert.Empty(TokenOption.Parse(Pay(Arbitrum + ",{\"id\":\"usdt-arbitrum\",\"type\":\"lightning\"}")));
        Assert.Equal(new[] { "USDT-ARBITRUM" }, TokenOption.Parse(Pay(Arbitrum + "," + SameId + "," + UpperId)).Select(o => o.Id));
    }

    [Fact]
    public void A_new_chain_within_a_supported_namespace_needs_no_code()
    {
        var options = TokenOption.Parse(Pay(Arbitrum +
            ",{\"id\":\"usdt-base\",\"type\":\"eip155\",\"asset\":\"eip155:8453/erc20:0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913\",\"unit\":\"USDT\"}"));
        Assert.Equal(new[] { "eip155:42161", "eip155:8453" }, options.Select(o => o.Asset.ChainId));
    }
}
