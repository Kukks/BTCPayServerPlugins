using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;
using static BTCPayServer.Plugins.SecSwitch.Tests.Fixtures;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryApplicabilityTests
{
    const string ConditionalFeed = """
        {
          "advisories": [],
          "conditionalAdvisories": [{
            "id": "combination", "identifier": "Plug", "affectedVersions": "<1.2.3", "severity": "high",
            "requires": {"identifier": "Dependency", "affectedVersions": ">=2.0.0 && <2.1.0"}
          }]
        }
        """;

    [Theory]
    [InlineData("1.2.2.0", "2.0.0.0", true)]
    [InlineData("1.2.3.0", "2.0.0.0", false)]
    [InlineData("1.2.2.0", "2.1.0.0", false)]
    [InlineData("1.2.2.0", null, false)]
    public void Both_loaded_plugins_must_be_in_their_respective_ranges(
        string targetVersion, string? dependencyVersion, bool expected)
    {
        var advisory = Assert.Single(AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(ConditionalFeed), out _));
        var plugins = new Dictionary<string, Version> { ["PLUG"] = Version.Parse(targetVersion) };
        if (dependencyVersion is not null)
            plugins.Add("dependency", Version.Parse(dependencyVersion));

        var state = new InstanceState(plugins, Version.Parse("2.4.7"), false);
        var applicable = AdvisoryApplicability.IsApplicable(advisory, state, out var installed, out var reason);

        Assert.Equal(expected, applicable);
        Assert.Equal(expected ? Version.Parse(targetVersion) : null, installed);
        if (expected)
            Assert.Contains("Dependency 2.0.0.0", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_dependency_version_is_indeterminate()
    {
        var advisory = Assert.Single(AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(ConditionalFeed), out _));
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.2.2.0"), ["Dependency"] = null! },
            Version.Parse("2.4.7"), false);

        var applicable = AdvisoryApplicability.IsApplicable(advisory, state, out var installed, out var reason);

        Assert.False(applicable);
        Assert.Null(installed);
        Assert.Contains(AdvisoryApplicability.IndeterminateVersionPhrase, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Plug", "1.2.1", "2.0.0", "1.2.2", true)]
    [InlineData("Plug", "1.2.2", "2.0.0", "1.2.3", false)]
    [InlineData("dependency", "1.2.2", "1.9.0", "2.0.0", true)]
    [InlineData("dependency", "1.2.3", "1.9.0", "2.0.0", false)]
    [InlineData("Dependency", "1.2.2", "2.0.0", "2.1.0", false)]
    [InlineData("Other", "1.2.2", "2.0.0", "2.0.0", false)]
    public void Candidate_updates_are_checked_against_both_affected_plugins(
        string identifier, string target, string dependency, string candidate, bool expected)
    {
        var advisory = Assert.Single(AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(ConditionalFeed), out _));
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = Version.Parse(target), ["Dependency"] = Version.Parse(dependency) },
            Version.Parse("2.4.7"), false);

        Assert.Equal(expected,
            AdvisoryApplicability.IsApplicableAfterUpdate(advisory, state, identifier, Version.Parse(candidate)));
    }

    [Fact]
    public void Applicable_when_installed_version_is_in_range()
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv(), State("1.1.0"), out var installed, out _);

        Assert.True(ok);
        Assert.Equal(Version.Parse("1.1.0"), installed);
    }

    [Fact]
    public void Not_applicable_when_installed_version_is_outside_range()
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv(), State("1.2.3"), out var installed, out var reason);

        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("not affected", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Not_applicable_when_plugin_is_not_installed()
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv(identifier: "Other"), State(), out _, out var reason);

        Assert.False(ok);
        Assert.Contains("not installed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("BTCPayServer")]
    [InlineData("btcpayserver")]
    public void Core_advisories_match_against_the_core_version_case_insensitively(string identifier)
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv(identifier: identifier, affected: "<2.5.0"), State(coreVersion: "2.4.2"), out var installed, out _);

        Assert.True(ok);
        Assert.Equal(Version.Parse("2.4.2"), installed);
    }

    [Theory]
    [InlineData("Plug")]
    [InlineData("plug")]
    [InlineData("PLUG")]
    public void Plugin_lookup_is_case_insensitive_whatever_comparer_the_dictionary_uses(string identifier)
    {
        var caseSensitive = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = Version.Parse("1.1.0") }, Version.Parse("2.4.7"), false);

        Assert.True(AdvisoryApplicability.IsApplicable(Adv(identifier: identifier), caseSensitive, out _, out _));
        Assert.True(AdvisoryApplicability.IsApplicable(Adv(identifier: identifier), State(), out _, out _));
    }

    [Fact]
    public void Malformed_version_condition_fails_closed()
    {
        // Space-separated ranges are not VersionCondition syntax; they must never read as an unconditional match.
        var ok = AdvisoryApplicability.IsApplicable(Adv(affected: ">=1.0.0 <1.2.3"), State(), out var installed, out var reason);

        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains("could not be parsed", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_condition_does_not_match_every_version(string affected)
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv(affected: affected), State(), out var installed, out _);

        Assert.False(ok);
        Assert.Null(installed);
    }

    [Theory]
    [InlineData(">=1.0.0")]
    [InlineData("<2.5.0")]
    public void An_unknown_core_version_is_indeterminate_not_a_silent_no_match(string affected)
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv(identifier: "BTCPayServer", affected: affected), State(coreVersion: null), out var installed, out var reason);

        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains(AdvisoryApplicability.IndeterminateVersionPhrase, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(">=1.0.0")]
    [InlineData("<1.2.3")]
    public void An_unknown_plugin_version_is_indeterminate_not_a_silent_no_match(string affected)
    {
        var state = new InstanceState(
            new Dictionary<string, Version> { ["Plug"] = null! }, Version.Parse("2.4.7"), false);

        var ok = AdvisoryApplicability.IsApplicable(Adv(affected: affected), state, out var installed, out var reason);

        Assert.False(ok);
        Assert.Null(installed);
        Assert.Contains(AdvisoryApplicability.IndeterminateVersionPhrase, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<=1.2.2", "1.2.2.0", true)]
    [InlineData("==1.2.2", "1.2.2.0", true)]
    [InlineData(">=1.2.3", "1.2.3.0", true)]
    [InlineData("<1.2.3", "1.2.3.0", false)]
    [InlineData(">1.2.2", "1.2.2.0", false)]
    [InlineData("!=1.2.3", "1.2.3.0", false)]
    [InlineData(">=1.0.0 && <1.2.3", "1.2.2.5", true)]
    [InlineData(">=1.0.0 && <1.2.3", "1.2.3.1", false)]
    [InlineData("<1.0.0 || >=2.0.0", "2.0.0.0", true)]
    [InlineData(">=1.0.0 && <2.0.0 || >=3.0.0 && <4.0.0", "3.1.0.0", true)]
    [InlineData("^1.2.0", "1.9.0.0", true)]
    public void Four_part_installed_versions_compare_correctly_against_three_part_conditions(
        string affected, string installed, bool expected)
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv(affected: affected), State(installed), out var version, out _);

        Assert.Equal(expected, ok);
        Assert.Equal(expected ? Version.Parse(installed) : null, version);
    }

    [Theory]
    [InlineData("<=2.4.7", true)]
    [InlineData("==2.4.7", true)]
    [InlineData("<2.4.7", false)]
    public void A_three_part_core_version_compares_correctly_against_the_same_boundary(string affected, bool expected)
    {
        var ok = AdvisoryApplicability.IsApplicable(
            Adv(identifier: "BTCPayServer", affected: affected), State(coreVersion: "2.4.7"), out _, out _);

        Assert.Equal(expected, ok);
    }

    [Theory]
    [InlineData("<1.0.0 || ||")]
    [InlineData("<1.0.0 || ")]
    [InlineData(">=1.0.0 && <2.0.0 || ||")]
    public void A_blank_clause_inside_a_condition_does_not_match_every_version(string affected)
    {
        var ok = AdvisoryApplicability.IsApplicable(Adv(affected: affected), State("5.0.0"), out _, out _);

        Assert.False(ok);
        Assert.False(AdvisoryApplicability.TryParseCondition(affected, out _));
    }

    [Theory]
    [InlineData("<1.0.0||||>=2.0.0")]
    [InlineData(">=1.0.0&&&&<2.0.0")]
    [InlineData("||<1.0.0")]
    [InlineData("&&<1.0.0")]
    [InlineData("<1.0.0||&&>=2.0.0&&<3.0.0")]
    [InlineData(">=2.0.0&&<3.0.0&&||<1.0.0")]
    public void Doubled_or_dangling_separators_are_rejected_because_core_drops_them_silently(string affected)
    {
        Assert.False(AdvisoryApplicability.TryParseCondition(affected, out _));
        Assert.False(AdvisoryApplicability.IsApplicable(Adv(affected: affected), State("1.5.0"), out _, out _));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public void Pad_fills_missing_parts_with_zero(string version, string expected)
    {
        Assert.Equal(Version.Parse(expected), AdvisoryApplicability.Pad(Version.Parse(version)));
    }
}
