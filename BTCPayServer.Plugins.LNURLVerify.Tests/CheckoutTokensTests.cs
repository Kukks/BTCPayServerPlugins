using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class CheckoutTokensTests
{
    static LnurlTokenPromptDetails Details() => new()
    {
        Networks =
        {
            new TokenNetworkState { OptionId = "usdt-arbitrum" },
            new TokenNetworkState { OptionId = "usdt-solana", Quote = new TokenQuoteState() },
            new TokenNetworkState { OptionId = "usdt-tron", Refused = true }
        }
    };

    [Fact]
    public void Opening_the_tab_requests_every_network_not_yet_quoted_or_refused() =>
        Assert.Equal(new[] { "usdt-arbitrum" }, CheckoutRails.PlanTokens(Details(), null, activateAllOnOpen: true));

    [Fact]
    public void With_the_setting_off_opening_the_tab_requests_nothing() =>
        Assert.Empty(CheckoutRails.PlanTokens(Details(), null, activateAllOnOpen: false));

    [Fact]
    public void A_tap_requests_that_network_even_when_already_quoted() =>
        Assert.Equal(new[] { "usdt-solana" }, CheckoutRails.PlanTokens(Details(), "usdt-solana", activateAllOnOpen: false));

    [Fact]
    public void A_tap_on_a_refused_or_unknown_network_requests_nothing()
    {
        Assert.Empty(CheckoutRails.PlanTokens(Details(), "usdt-tron", activateAllOnOpen: true));
        Assert.Empty(CheckoutRails.PlanTokens(Details(), "usdt-polygon", activateAllOnOpen: true));
        Assert.Empty(CheckoutRails.PlanTokens(null, "usdt-arbitrum", activateAllOnOpen: true));
    }
}
