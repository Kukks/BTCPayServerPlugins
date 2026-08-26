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
            Adv("Plug", ">=1.0.0 && <1.2.3"), State("Plug", "1.2.3"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("not affected", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Not_applicable_when_plugin_is_not_installed()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Other", ">=1.0.0"), State("Plug", "1.1.0"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
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
            Adv("Plug", ">=1.0.0 <1.2.3"), State("Plug", "1.1.0"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("could not be parsed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_condition_does_not_silently_match_everything()
    {
        // VersionCondition.TryParse("") returns a `Yes` that matches ALL versions. An advisory with a
        // blank range must be rejected upstream by the parser, but assert defence in depth here too.
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", "   "), State("Plug", "1.1.0"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Core_identifier_match_is_case_insensitive()
    {
        // Guards against a regression to an ordinal (case-sensitive) comparison for the Core
        // match: only "BTCPayServer" is used elsewhere in this file, which would still pass
        // even if OrdinalIgnoreCase were dropped.
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("btcpayserver", "<2.5.0"), State("Plug", "1.0.0", coreVersion: "2.4.2"), out var installed, out _);
        Assert.True(ok);
        Assert.Equal(Version.Parse("2.4.2"), installed);
    }

    [Fact]
    public void Plugin_lookup_is_case_insensitive_with_a_case_sensitive_dictionary()
    {
        // State(...) builds a plain Dictionary, which uses the case-sensitive default comparer.
        // The advisory identifier here differs in case from the installed key to prove
        // IsApplicable performs its own case-insensitive comparison rather than depending on
        // whatever comparer the caller happened to build the dictionary with.
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("plug", ">=1.0.0 && <1.2.3"), State("Plug", "1.1.0"), out var installed, out _);
        Assert.True(ok);
        Assert.Equal(Version.Parse("1.1.0"), installed);
    }

    [Fact]
    public void Plugin_lookup_is_case_insensitive_with_an_ordinal_ignore_case_dictionary()
    {
        // Mirrors how BTCPayServer core itself builds the installed-plugin dictionary
        // (PluginService.cs uses StringComparer.OrdinalIgnoreCase). Included alongside the
        // case-sensitive-dictionary test above to show the match holds either way.
        var state = new InstanceState(
            new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase) { ["Plug"] = Version.Parse("1.1.0") },
            Version.Parse("2.4.2"), CanUseSsh: false);
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("PLUG", ">=1.0.0 && <1.2.3"), state, out var installed, out _);
        Assert.True(ok);
        Assert.Equal(Version.Parse("1.1.0"), installed);
    }

    [Fact]
    public void Null_advisory_fails_closed_without_throwing()
    {
        var ok = AdvisoryApplicability.IsApplicable(null!, State("Plug", "1.1.0"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Null_state_fails_closed_without_throwing()
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv("Plug", ">=1.0.0"), null!, out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Null_installed_plugins_map_fails_closed_without_throwing()
    {
        var state = new InstanceState(null!, Version.Parse("2.4.2"), CanUseSsh: false);
        var ok = AdvisoryApplicability.IsApplicable(Adv("Plug", ">=1.0.0"), state, out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Null_identifier_fails_closed_without_throwing()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv(null!, ">=1.0.0"), State("Plug", "1.1.0"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Blank_identifier_fails_closed_without_throwing()
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("   ", ">=1.0.0"), State("Plug", "1.1.0"), out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Null_core_version_with_a_would_otherwise_fail_open_operator_is_indeterminate()
    {
        // Without the dedicated guard, System.Version's operators treat null as "less than
        // everything", so null >= 1.0.0 evaluates to false - reading as an ordinary, silent
        // "not affected" outcome rather than the indeterminate state it actually is.
        var state = new InstanceState(new Dictionary<string, Version>(), null!, CanUseSsh: false);
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("BTCPayServer", ">=1.0.0"), state, out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("could not be determined", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_core_version_with_a_would_otherwise_be_a_misleading_match_is_indeterminate()
    {
        // Without the dedicated guard, null < 2.5.0 evaluates to true, which would make
        // IsApplicable report ok=true while installedVersion is null - "applicable, but we
        // don't know the version" is not a real answer.
        var state = new InstanceState(new Dictionary<string, Version>(), null!, CanUseSsh: false);
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("BTCPayServer", "<2.5.0"), state, out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("could not be determined", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_plugin_version_with_a_would_otherwise_fail_open_operator_is_indeterminate()
    {
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = null! }, Version.Parse("2.4.2"), CanUseSsh: false);
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", ">=1.0.0"), state, out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("could not be determined", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_plugin_version_with_a_would_otherwise_be_a_misleading_match_is_indeterminate()
    {
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = null! }, Version.Parse("2.4.2"), CanUseSsh: false);
        var ok = AdvisoryApplicability.IsApplicable(
            Adv("Plug", "<1.2.3"), state, out var installed, out var reason);
        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("could not be determined", reason, StringComparison.OrdinalIgnoreCase);
    }
}
