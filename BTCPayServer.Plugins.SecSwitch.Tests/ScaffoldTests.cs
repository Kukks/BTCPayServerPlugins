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
    public void Bouncycastle_assembly_major_version_is_2()
    {
        // Does NOT guard the explicit PackageReference in the plugin csproj: compile-time visibility of
        // Org.BouncyCastle.Bcpg.OpenPgp types flows through a transitive path too (MimeKit/ExchangeSharp,
        // reached via BTCPayServer.csproj, whose ItemDefinitionGroup ExcludeAssets omits "compile"), so
        // this passes whether or not the explicit reference exists. What it does guard: a silent major-version
        // drift (e.g. a future transitive bump to BouncyCastle 3.x), where the OpenPGP API surface
        // AdvisoryVerifier depends on may change shape. Asserting only .Major (not the full 2.6.2 package
        // version) is deliberate - the NuGet package version does not equal the assembly version; the
        // resolved assembly here reports 2.0.0.0.
        var version = typeof(Org.BouncyCastle.Bcpg.OpenPgp.PgpSignature).Assembly.GetName().Version;
        Assert.True(version?.Major == 2,
            $"BouncyCastle.Cryptography assembly major version drifted from 2 to {version} - " +
            "the OpenPGP API surface AdvisoryVerifier depends on may have changed shape.");
    }
}
