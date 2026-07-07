using BTCPayServer.Plugins.Electrum;
using Microsoft.Extensions.Configuration;
using Xunit;

public class ElectrumGuardTests
{
    [Fact]
    public void AllowNonMainnet_defaults_false()
    {
        Assert.False(new ElectrumSettings().AllowNonMainnet);
    }

    [Fact]
    public void Escape_flag_is_read_from_configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ELECTRUM_ALLOWNONMAINNET"] = "true" })
            .Build();
        Assert.True(ElectrumPlugin.AllowNonMainnet(config));
    }
}
