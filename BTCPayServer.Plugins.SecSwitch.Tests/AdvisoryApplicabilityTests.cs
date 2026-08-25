using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryApplicabilityTests
{
    static Advisory Adv(string identifier, string affected) => new()
    {
        Id = "x", Identifier = identifier, AffectedVersions = affected,
        Severity = AdvisorySeverity.High, Title = "t"
    };

    static InstanceState State(string pluginId, string pluginVersion, string coreVersion = "2.4.2") =>
        new(new Dictionary<string, Version> { [pluginId] = Version.Parse(pluginVersion) },
            Version.Parse(coreVersion), CanUseSsh: false);

    [Fact]
    public void Applicable_when_installed_version_is_in_range()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", ">=1.0.0 && <1.2.3"), State("Plug", "1.1.0"), out var installed, out _);
        Assert.True(ok);
        Assert.Equal(Version.Parse("1.1.0"), installed);
    }

    [Fact]
    public void Not_applicable_when_installed_version_is_outside_range()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", ">=1.0.0 && <1.2.3"), State("Plug", "1.2.3"), out _, out var reason);
        Assert.False(ok);
        Assert.Contains("not affected", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Not_applicable_when_plugin_is_not_installed()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Other", ">=1.0.0"), State("Plug", "1.1.0"), out _, out var reason);
        Assert.False(ok);
        Assert.Contains("not installed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_advisories_match_against_the_core_version()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("BTCPayServer", "<2.5.0"), State("Plug", "1.0.0", coreVersion: "2.4.2"), out var installed, out _);
        Assert.True(ok);
        Assert.Equal(Version.Parse("2.4.2"), installed);
    }

    [Fact]
    public void Malformed_version_condition_fails_closed()
    {
        // ">=1.0.0 <1.2.3" (space-separated) is NOT valid VersionCondition syntax — core's parser
        // splits only on && and ||. It must be rejected, never treated as an unconditional match.
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", ">=1.0.0 <1.2.3"), State("Plug", "1.1.0"), out _, out var reason);
        Assert.False(ok);
        Assert.Contains("could not be parsed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_condition_does_not_silently_match_everything()
    {
        // VersionCondition.TryParse("") returns a `Yes` that matches ALL versions. An advisory with a
        // blank range must be rejected upstream by the parser, but assert defence in depth here too.
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", "   "), State("Plug", "1.1.0"), out _, out var reason);
        Assert.False(ok);
        Assert.NotEmpty(reason);
    }
}
