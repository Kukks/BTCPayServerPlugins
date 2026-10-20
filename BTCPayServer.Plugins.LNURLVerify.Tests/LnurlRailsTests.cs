using BTCPayServer.Payments;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailsTests
{
    [Fact]
    public void Rails_are_found_by_payment_method_id()
    {
        Assert.Same(LnurlRails.Arkade, LnurlRails.For(new PaymentMethodId("LNURL-ARKADE")));
        Assert.True(LnurlRails.IsRail(PaymentMethodId.Parse("LNURL-ONCHAIN")));
        Assert.False(LnurlRails.IsRail(PaymentMethodId.Parse("BTC-LN")));
    }

    [Theory]
    [InlineData("tark1qexample", false, true)]
    [InlineData("ark1qexample", false, false)]
    [InlineData("ark1qexample", true, true)]
    [InlineData("tark1qexample", true, false)]
    public void An_arkade_destination_must_carry_the_networks_prefix(string destination, bool mainnet, bool valid) =>
        Assert.Equal(valid, LnurlRails.Arkade.IsValidDestination(destination, mainnet ? Network.Main : Network.RegTest));

    [Theory]
    [InlineData("tark1qr340x\" onmouseover=x")]
    [InlineData("tark1")]
    public void An_arkade_destination_outside_the_bech32_alphabet_is_refused(string destination) =>
        Assert.False(LnurlRails.Arkade.IsValidDestination(destination, Network.RegTest));

    [Fact]
    public void An_onchain_destination_must_be_an_address_of_the_network()
    {
        var regtest = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        Assert.True(LnurlRails.OnChain.IsValidDestination(regtest, Network.RegTest));
        Assert.False(LnurlRails.OnChain.IsValidDestination(regtest, Network.Main));
        Assert.False(LnurlRails.OnChain.IsValidDestination("tark1qexample", Network.RegTest));
    }
}
