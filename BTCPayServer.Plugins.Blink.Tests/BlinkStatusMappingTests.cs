using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blink;
using NBitcoin;
using Xunit;

// Tests for the custodial client's "payment status hooks": the mappings from Blink's GraphQL
// string statuses to BTCPay's invoice/payment/pay enums. These previously threw on unexpected
// input; the mappers now degrade unknown values safely.
public class BlinkStatusMappingTests
{
    [Theory]
    [InlineData("PAID", LightningInvoiceStatus.Paid)]
    [InlineData("EXPIRED", LightningInvoiceStatus.Expired)]
    [InlineData("PENDING", LightningInvoiceStatus.Unpaid)]
    [InlineData("SOMETHING_NEW", LightningInvoiceStatus.Unpaid)]
    [InlineData("", LightningInvoiceStatus.Unpaid)]
    [InlineData(null, LightningInvoiceStatus.Unpaid)]
    public void MapInvoiceStatus_maps_and_degrades_unknown_to_unpaid(string? status, LightningInvoiceStatus expected)
    {
        Assert.Equal(expected, BlinkLightningClient.MapInvoiceStatus(status));
    }

    [Theory]
    [InlineData("SUCCESS", LightningPaymentStatus.Complete)]
    [InlineData("FAILURE", LightningPaymentStatus.Failed)]
    [InlineData("PENDING", LightningPaymentStatus.Pending)]
    [InlineData("WAT", LightningPaymentStatus.Unknown)]
    [InlineData(null, LightningPaymentStatus.Unknown)]
    public void MapPaymentStatus_maps_and_degrades_unknown_to_unknown(string? status, LightningPaymentStatus expected)
    {
        Assert.Equal(expected, BlinkLightningClient.MapPaymentStatus(status));
    }

    [Theory]
    [InlineData("ALREADY_PAID", PayResult.Ok)]
    [InlineData("SUCCESS", PayResult.Ok)]
    [InlineData("FAILURE", PayResult.Error)]
    [InlineData("PENDING", PayResult.Unknown)]
    [InlineData("WAT", PayResult.Unknown)]
    [InlineData(null, PayResult.Unknown)]
    public void MapPayResult_maps_and_degrades_unknown_to_unknown(string? status, PayResult expected)
    {
        Assert.Equal(expected, BlinkLightningClient.MapPayResult(status));
    }

    [Theory]
    [InlineData("ALREADY_PAID", LightningPaymentStatus.Complete)]
    [InlineData("SUCCESS", LightningPaymentStatus.Complete)]
    [InlineData("FAILURE", LightningPaymentStatus.Failed)]
    [InlineData("PENDING", LightningPaymentStatus.Pending)]
    [InlineData("WAT", LightningPaymentStatus.Unknown)]
    [InlineData(null, LightningPaymentStatus.Unknown)]
    public void MapPayDetailStatus_maps_and_degrades_unknown_to_unknown(string? status, LightningPaymentStatus expected)
    {
        Assert.Equal(expected, BlinkLightningClient.MapPayDetailStatus(status));
    }

    [Theory]
    [InlineData("mainnet", "Main")]
    [InlineData("testnet", "TestNet")]
    // signet maps to TestNet (BTCPay has no distinct signet network here).
    [InlineData("signet", "TestNet")]
    [InlineData("regtest", "RegTest")]
    public void MapNetwork_maps_known_networks(string network, string expected)
    {
        Assert.Equal(Network.GetNetwork(expected), BlinkLightningClient.MapNetwork(network));
    }

    [Fact]
    public void MapNetwork_throws_on_unknown()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => BlinkLightningClient.MapNetwork("dogecoin"));
    }

    [Theory]
    [InlineData("BTC", BlinkCurrency.BTC)]
    [InlineData("USD", BlinkCurrency.USD)]
    public void ParseBlinkCurrency_maps_known(string input, BlinkCurrency expected)
    {
        Assert.Equal(expected, BlinkLightningClient.ParseBlinkCurrency(input));
    }

    [Fact]
    public void ParseBlinkCurrency_throws_on_unknown()
    {
        Assert.Throws<System.FormatException>(() => BlinkLightningClient.ParseBlinkCurrency("EUR"));
    }
}
