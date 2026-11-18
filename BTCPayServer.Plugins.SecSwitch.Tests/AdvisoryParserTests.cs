using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryParserTests
{
    const string Valid =
        """{"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <1.2.3","fixedVersion":"1.2.3","severity":"high","title":"T"}""";

    const string ValidRequirement = """{"identifier":"Dependency","affectedVersions":"<=2.0.0"}""";

    static byte[] Feed(params string[] entries) =>
        Encoding.UTF8.GetBytes("{\"advisories\":[" + string.Join(",", entries) + "]}");

    static string Conditional(string requirement = ValidRequirement, string id = "conditional") =>
        $$"""{"id":"{{id}}","identifier":"Plug","affectedVersions":">=1.0.0 && <1.2.3","severity":"high","requires":{{requirement}}}""";

    [Fact]
    public void Parses_a_valid_advisory()
    {
        var advisories = AdvisoryParser.ParseFeed(Feed(Valid), out var skipped);

        var advisory = Assert.Single(advisories);
        Assert.Empty(skipped);
        Assert.Equal("a1", advisory.Id);
        Assert.Equal("Plug", advisory.Identifier);
        Assert.Equal(">=1.0.0 && <1.2.3", advisory.AffectedVersions);
        Assert.Equal("1.2.3", advisory.FixedVersion);
        Assert.Equal(AdvisorySeverity.High, advisory.Severity);
        Assert.Equal("T", advisory.Title);
    }

    [Fact]
    public void Optional_fields_may_be_absent_and_unknown_fields_are_ignored()
    {
        var advisories = AdvisoryParser.ParseFeed(
            Feed("""{"id":"a","identifier":"P","affectedVersions":"<2.0.0","severity":"low","description":"x","references":["https://e"]}"""),
            out _);

        var advisory = Assert.Single(advisories);
        Assert.Null(advisory.FixedVersion);
        Assert.Equal("", advisory.Title);
    }

    [Theory]
    [InlineData("critical", AdvisorySeverity.Critical)]
    [InlineData("MEDIUM", AdvisorySeverity.Medium)]
    [InlineData(" Low ", AdvisorySeverity.Low)]
    public void Severity_is_case_insensitive_and_trimmed(string raw, AdvisorySeverity expected)
    {
        var advisories = AdvisoryParser.ParseFeed(
            Feed($$"""{"id":"a","identifier":"P","affectedVersions":"<2.0.0","severity":"{{raw}}"}"""), out _);

        Assert.Equal(expected, Assert.Single(advisories).Severity);
    }

    [Theory]
    [InlineData("""{"id":"a","identifier":"P","affectedVersions":"<2.0.0","severity":"bogus"}""")]
    [InlineData("""{"id":"a","identifier":"P","affectedVersions":"<2.0.0","severity":"7"}""")]
    [InlineData("""{"id":"a","identifier":"P","affectedVersions":"<2.0.0"}""")]
    [InlineData("""{"identifier":"P","affectedVersions":"<2.0.0","severity":"low"}""")]
    [InlineData("""{"id":"a","affectedVersions":"<2.0.0","severity":"low"}""")]
    [InlineData("""{"id":"a","identifier":"P","severity":"low"}""")]
    [InlineData("""{"id":" ","identifier":"P","affectedVersions":"<2.0.0","severity":"low"}""")]
    [InlineData("""{"id":"a","identifier":"P","affectedVersions":"","severity":"low"}""")]
    [InlineData("""{"id":"a","identifier":"P","affectedVersions":"   ","severity":"low"}""")]
    [InlineData("""{"id":1,"identifier":"P","affectedVersions":"<2.0.0","severity":"low"}""")]
    [InlineData("42")]
    [InlineData("[]")]
    public void Skips_entries_that_are_not_valid_advisories(string entry)
    {
        var advisories = AdvisoryParser.ParseFeed(Feed(entry), out var skipped);

        Assert.Empty(advisories);
        Assert.Single(skipped);
    }

    [Fact]
    public void One_bad_entry_does_not_hide_the_others()
    {
        var advisories = AdvisoryParser.ParseFeed(Feed("""{"id":"bad"}""", Valid), out var skipped);

        Assert.Equal("a1", Assert.Single(advisories).Id);
        Assert.StartsWith("advisories[0]:", Assert.Single(skipped).Reason);
    }

    [Fact]
    public void Duplicate_ids_keep_the_first_entry()
    {
        var second = Valid.Replace("\"id\":\"a1\"", "\"id\":\"A1\"").Replace("\"high\"", "\"low\"");

        var advisories = AdvisoryParser.ParseFeed(Feed(Valid, second), out var skipped);

        Assert.Equal(AdvisorySeverity.High, Assert.Single(advisories).Severity);
        var duplicate = Assert.Single(skipped);
        Assert.Equal("A1", duplicate.Id);
        Assert.Contains("duplicate", duplicate.Reason);
    }

    [Fact]
    public void Direct_and_conditional_entries_can_share_the_same_feed()
    {
        var feed = $$"""{"advisories":[{{Valid}}],"conditionalAdvisories":[{{Conditional()}}]}""";

        var advisories = AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(feed), out var skipped);

        Assert.Equal(["a1", "conditional"], advisories.Select(advisory => advisory.Id));
        Assert.Empty(skipped);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"identifier":"Dependency","affectedVersions":"<=2.0.0 || "}""")]
    [InlineData("""{"identifier":"pLuG","affectedVersions":"<=2.0.0"}""")]
    [InlineData("""{"identifier":"btcpayserver","affectedVersions":"<=2.0.0"}""")]
    [InlineData("""{"identifier":"Dependency","affectedVersions":"<=2.0.0","other":true}""")]
    public void Invalid_conditional_requirements_are_skipped_with_their_id_retained(string requirement)
    {
        var feed = $$"""{"advisories":[],"conditionalAdvisories":[{{Conditional(requirement)}}]}""";

        var advisories = AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(feed), out var skipped);

        Assert.Empty(advisories);
        Assert.Equal("conditional", Assert.Single(skipped).Id);
    }

    [Fact]
    public void A_conditional_advisory_in_the_direct_array_cannot_trigger_an_unconditional_action()
    {
        var advisories = AdvisoryParser.ParseFeed(Feed(Conditional()), out var skipped);

        Assert.Empty(advisories);
        Assert.Equal("conditional", Assert.Single(skipped).Id);
    }

    [Fact]
    public void A_core_advisory_cannot_depend_on_a_plugin()
    {
        var entry = Conditional().Replace("\"Plug\"", "\"BTCpayserver\"");
        var feed = $$"""{"advisories":[],"conditionalAdvisories":[{{entry}}]}""";

        var advisories = AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(feed), out var skipped);

        Assert.Empty(advisories);
        Assert.Equal("conditional", Assert.Single(skipped).Id);
    }

    [Fact]
    public void A_duplicate_across_arrays_does_not_trigger_either_version_of_the_advisory()
    {
        var feed = $$"""{"advisories":[{{Valid}}],"conditionalAdvisories":[{{Conditional(id: "A1")}}]}""";

        var advisories = AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(feed), out var skipped);

        Assert.Empty(advisories);
        Assert.Contains(skipped, entry => entry.Id == "A1" && entry.Reason.Contains("duplicate"));
    }

    [Theory]
    [InlineData("bad", """{"id":"bad","identifier":"Plug","affectedVersions":"<1.2.3","severity":"high","requires":{"identifier":"Dep","affectedVersions":"\uD800"}}""")]
    [InlineData(null, """{"id":"\uD800","identifier":"Plug","affectedVersions":"<1.2.3","severity":"high","requires":{"identifier":"Dep","affectedVersions":"<2.0.0"}}""")]
    public void Undecodable_text_skips_only_that_entry_and_preserves_any_readable_id(string? id, string entry)
    {
        var feed = $$"""{"advisories":[{{Valid}}],"conditionalAdvisories":[{{entry}},{{Conditional()}}]}""";

        var advisories = AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(feed), out var skipped);

        Assert.Equal(["a1", "conditional"], advisories.Select(advisory => advisory.Id));
        Assert.Equal(id, Assert.Single(skipped).Id);
    }

    [Theory]
    [InlineData(">=1.0.0 <1.2.3")]
    [InlineData("<1.0.0 || ")]
    [InlineData("<1.0.0 || ||")]
    [InlineData(">=1.0.0 && <2.0.0 || ||")]
    [InlineData(">=1.0.0 && ")]
    [InlineData("1.0.0")]
    [InlineData("<1.0.0||||>=2.0.0")]
    [InlineData(">=1.0.0&&&&<2.0.0")]
    [InlineData("<abc")]
    public void An_affectedVersions_that_is_not_a_strict_condition_is_skipped_and_keeps_its_id(string affected)
    {
        var entry = $$"""{"id":"x","identifier":"P","affectedVersions":"{{affected}}","severity":"low"}""";

        var advisories = AdvisoryParser.ParseFeed(Feed(entry), out var skipped);

        Assert.Empty(advisories);
        Assert.Equal("x", Assert.Single(skipped).Id);
    }

    [Theory]
    [InlineData("""{"identifier":"P","affectedVersions":"<1.0.0","severity":"low"}""")]
    [InlineData("42")]
    public void A_skipped_entry_without_a_usable_id_reports_none(string entry)
    {
        AdvisoryParser.ParseFeed(Feed(entry), out var skipped);

        Assert.Null(Assert.Single(skipped).Id);
    }

    [Fact]
    public void An_empty_list_is_a_valid_feed()
    {
        Assert.Empty(AdvisoryParser.ParseFeed(Feed(), out var skipped));
        Assert.Empty(skipped);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"advisories":{}}""")]
    [InlineData("""{"advisories":null}""")]
    [InlineData("""{"advisories":[],"conditionalAdvisories":null}""")]
    [InlineData("""{"advisories":[],"conditionalAdvisories":{}}""")]
    [InlineData("""{"advisories":[{"id":"core"}],"advisories":[]}""")]
    [InlineData("""{"advisories":[],"conditionalAdvisories":[{"id":"combo"}],"conditionalAdvisories":[]}""")]
    public void A_malformed_envelope_throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(json), out _));
    }
}
