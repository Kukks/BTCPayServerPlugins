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
}
