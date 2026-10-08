using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class CheckoutTokensTests
{
    const long Due = 20_000_000;
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    static TokenNetworkState Quoted(string id, long amountMsat = Due, long? expiresAt = 1_790_000_600) =>
        new() { OptionId = id, Quote = new TokenQuoteState { AmountMsat = amountMsat, ExpiresAt = expiresAt } };

    static IReadOnlyCollection<string> Plan(string? network, params TokenNetworkState[] networks) =>
        CheckoutRails.PlanTokens(new LnurlTokenPromptDetails { Networks = networks.ToList() }, network, Due, Now);

    [Fact]
    public void Opening_the_tab_requests_nothing() =>
        Assert.Empty(Plan(null, new TokenNetworkState { OptionId = "ff-usdtarbitrum" }, new TokenNetworkState { OptionId = "ff-usdttrc" }));

    [Fact]
    public void A_tap_on_a_network_holding_a_live_quote_requests_nothing()
    {
        Assert.Empty(Plan("ff-usdtarbitrum", Quoted("ff-usdtarbitrum")));
        Assert.Empty(Plan("ff-usdtarbitrum", Quoted("ff-usdtarbitrum", expiresAt: null)));
    }

    [Theory]
    [InlineData("unrequested")]
    [InlineData("failed")]
    [InlineData("expired")]
    [InlineData("stale")]
    public void A_tap_requests_a_network_whose_quote_is_not_live(string state)
    {
        var network = state switch
        {
            "unrequested" => new TokenNetworkState { OptionId = "ff-usdtarbitrum" },
            "failed" => new TokenNetworkState { OptionId = "ff-usdtarbitrum", FailedAt = 1 },
            "expired" => Quoted("ff-usdtarbitrum", expiresAt: 1_790_000_000),
            _ => Quoted("ff-usdtarbitrum", amountMsat: Due + 1000)
        };
        Assert.Equal(state, network.State(Due, Now));
        Assert.Equal(new[] { "ff-usdtarbitrum" }, Plan("ff-usdtarbitrum", network));
    }

    [Fact]
    public void A_tap_on_a_refused_or_unknown_network_requests_nothing()
    {
        Assert.Empty(Plan("usdt-tron", new TokenNetworkState { OptionId = "usdt-tron", Refused = true }));
        Assert.Empty(Plan("usdt-polygon", new TokenNetworkState { OptionId = "usdt-arbitrum" }));
        Assert.Empty(CheckoutRails.PlanTokens(null, "usdt-arbitrum", Due, Now));
    }
}
