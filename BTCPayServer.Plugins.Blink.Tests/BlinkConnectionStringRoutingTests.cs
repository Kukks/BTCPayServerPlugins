using System.Collections.Generic;
using BTCPayServer.Plugins.Blink;
using NBitcoin;
using Xunit;

// Tests for the connection-string handler's pure routing decisions: whether a blink connection
// string is treated as a non-custodial (ln-address) account, and the default server per network.
public class BlinkConnectionStringRoutingTests
{
    private static Dictionary<string, string> Kv(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void LnAddress_routes_non_custodial()
    {
        var ok = BlinkLightningConnectionStringHandler.IsLnAddressConnectionString(
            Kv(("ln-address", "twentyone@blink.sv")), out var lnAddress);
        Assert.True(ok);
        Assert.Equal("twentyone@blink.sv", lnAddress);
    }

    [Fact]
    public void Username_alias_routes_non_custodial()
    {
        var ok = BlinkLightningConnectionStringHandler.IsLnAddressConnectionString(
            Kv(("username", "twentyone@blink.sv")), out var lnAddress);
        Assert.True(ok);
        Assert.Equal("twentyone@blink.sv", lnAddress);
    }

    [Fact]
    public void ApiKey_present_is_custodial_even_with_ln_address()
    {
        // An api-key takes precedence: this is a custodial connection, not routed to the ln-address client.
        var ok = BlinkLightningConnectionStringHandler.IsLnAddressConnectionString(
            Kv(("api-key", "blink_abc"), ("ln-address", "twentyone@blink.sv")), out var lnAddress);
        Assert.False(ok);
        Assert.Null(lnAddress);
    }

    [Fact]
    public void ApiKey_only_is_custodial()
    {
        var ok = BlinkLightningConnectionStringHandler.IsLnAddressConnectionString(
            Kv(("api-key", "blink_abc"), ("wallet-id", "xyz")), out var lnAddress);
        Assert.False(ok);
        Assert.Null(lnAddress);
    }

    [Fact]
    public void Neither_key_is_not_ln_address()
    {
        var ok = BlinkLightningConnectionStringHandler.IsLnAddressConnectionString(
            Kv(("server", "https://api.blink.sv/graphql")), out var lnAddress);
        Assert.False(ok);
        Assert.Null(lnAddress);
    }

    [Fact]
    public void DefaultServer_mainnet()
    {
        Assert.Equal("https://api.blink.sv/graphql", BlinkLightningConnectionStringHandler.DefaultServer(Network.Main));
    }

    [Fact]
    public void DefaultServer_testnet()
    {
        Assert.Equal("https://api.staging.galoy.io/graphql",
            BlinkLightningConnectionStringHandler.DefaultServer(Network.TestNet));
    }

    [Fact]
    public void DefaultServer_regtest()
    {
        Assert.Equal("http://localhost:4455/graphql",
            BlinkLightningConnectionStringHandler.DefaultServer(Network.RegTest));
    }
}
