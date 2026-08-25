using BTCPayServer.Plugins.SecSwitch;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Plugin_declares_btcpayserver_dependency()
    {
        var plugin = new SecSwitchPlugin();
        var dep = Assert.Single(plugin.Dependencies);
        Assert.Equal("BTCPayServer", dep.Identifier);
        Assert.Equal(">=2.4.2", dep.Condition);
    }

    [Fact]
    public void Bouncycastle_openpgp_types_are_reachable()
    {
        // Guards the explicit PackageReference: if this fails, PGP verification cannot be built.
        Assert.NotNull(typeof(Org.BouncyCastle.Bcpg.OpenPgp.PgpSignature));
    }
}
