using System.Linq;
using BTCPayServer.Plugins.LNURLVerify;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class PaymentOptionTests
{
    [Fact]
    public void Parse_skips_malformed_entries_and_treats_absent_available_as_true()
    {
        var options = PaymentOption.Parse(JObject.Parse(
            "{\"paymentOptions\":[{\"id\":\"lightning\",\"type\":\"lightning\"},{\"type\":\"arkade\"},{\"id\":\"x\"},42," +
            "{\"id\":\"onchain\",\"type\":\"onchain\",\"available\":false,\"minSendable\":10000000}]}"));

        Assert.Equal(new[] { "lightning", "onchain" }, options.Select(o => o.Id));
        Assert.True(options[0].Available);
        Assert.Null(options[0].MinSendable);
        Assert.False(options[1].Available);
        Assert.Equal(10_000_000L, options[1].MinSendable);
    }

    [Fact]
    public void A_provider_is_read_off_an_option_when_it_is_a_short_string()
    {
        var options = PaymentOption.Parse(JObject.Parse("{\"paymentOptions\":[{\"id\":\"a\",\"type\":\"tron\",\"provider\":\"FixedFloat\"}," +
            "{\"id\":\"b\",\"type\":\"tron\"},{\"id\":\"c\",\"type\":\"tron\",\"provider\":7}," +
            "{\"id\":\"d\",\"type\":\"tron\",\"provider\":\"" + new string('x', 65) + "\"}]}"));
        Assert.Equal(new[] { "FixedFloat", null, null, null }, options.Select(o => o.Provider));
    }

    [Theory]
    [InlineData("100000000000000000000")]
    [InlineData("1e300")]
    [InlineData("-5")]
    public void A_bound_no_msat_amount_can_hold_is_ignored_rather_than_breaking_lightning(string bound)
    {
        var pay = JObject.Parse("{\"minSendable\":2000,\"maxSendable\":9000,\"paymentOptions\":[" +
                                "{\"id\":\"usdt-eth\",\"type\":\"eip155\",\"minSendable\":" + bound + ",\"maxSendable\":" + bound + "}," +
                                "{\"id\":\"ln\",\"type\":\"lightning\"}]}");

        var token = PaymentOption.Parse(pay)[0];
        Assert.Equal(((long?)null, (long?)null), (token.MinSendable, token.MaxSendable));
        Assert.Equal((2000L, 9000L, (string?)"ln"), PaymentOption.PlanLightning(pay, 1, long.MaxValue));
    }

    [Fact]
    public void PlanLightning_without_options_keeps_the_top_level_bounds()
    {
        var plan = PaymentOption.PlanLightning(JObject.Parse("{\"minSendable\":2000,\"maxSendable\":9000}"), 1, long.MaxValue);

        Assert.Equal((2000L, 9000L, (string?)null), plan);
    }

    [Fact]
    public void PlanLightning_takes_each_bound_from_the_option_or_else_the_top_level()
    {
        var plan = PaymentOption.PlanLightning(JObject.Parse(
            "{\"minSendable\":2000,\"maxSendable\":9000,\"paymentOptions\":[{\"id\":\"ln\",\"type\":\"Lightning\",\"minSendable\":3000}]}"),
            1, long.MaxValue);

        Assert.Equal((3000L, 9000L, (string?)"ln"), plan);
    }

    [Fact]
    public void PlanLightning_skips_an_unavailable_lightning_option_for_an_available_one()
    {
        var plan = PaymentOption.PlanLightning(JObject.Parse(
            "{\"paymentOptions\":[{\"id\":\"ln-down\",\"type\":\"lightning\",\"available\":false},{\"id\":\"ln\",\"type\":\"lightning\",\"minSendable\":3000}]}"),
            1, long.MaxValue);

        Assert.Equal((3000L, long.MaxValue, (string?)"ln"), plan);
    }

    [Fact]
    public void PlanLightning_refuses_when_every_lightning_option_is_unavailable() =>
        Assert.Throws<System.NotSupportedException>(() => PaymentOption.PlanLightning(JObject.Parse(
            "{\"paymentOptions\":[{\"id\":\"ln\",\"type\":\"lightning\",\"available\":false},{\"id\":\"arkade\",\"type\":\"arkade\"}]}"),
            1, long.MaxValue));
}
