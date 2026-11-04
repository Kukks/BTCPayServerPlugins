using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryParserTests
{
    const string Valid =
        """{"id":"a1","identifier":"Plug","affectedVersions":">=1.0.0 && <1.2.3","fixedVersion":"1.2.3","severity":"high","title":"T"}""";

    static byte[] Feed(params string[] entries) =>
        Encoding.UTF8.GetBytes("{\"advisories\":[" + string.Join(",", entries) + "]}");

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
        Assert.Contains("duplicate", Assert.Single(skipped).Reason);
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
    public void A_malformed_envelope_throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => AdvisoryParser.ParseFeed(Encoding.UTF8.GetBytes(json), out _));
    }
}
