using System.Text;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryParserTests
{
    const string Valid = """
    {
      "id": "2026-08-25-electrum-xss",
      "identifier": "BTCPayServer.Plugins.Electrum",
      "affectedVersions": ">=1.0.0 && <1.2.3",
      "fixedVersion": "1.2.3",
      "severity": "high",
      "title": "Stored XSS",
      "description": "Details here.",
      "references": ["https://example.com/a"],
      "publishedAt": "2026-08-25T00:00:00Z",
      "supersedes": null,
      "revoked": false
    }
    """;

    [Fact]
    public void Parses_a_valid_advisory()
    {
        Assert.True(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(Valid), out var a, out var err));
        Assert.Null(err);
        Assert.NotNull(a);
        Assert.Equal("2026-08-25-electrum-xss", a!.Id);
        Assert.Equal("BTCPayServer.Plugins.Electrum", a.Identifier);
        Assert.Equal(AdvisorySeverity.High, a.Severity);
        Assert.Equal("1.2.3", a.FixedVersion);
        Assert.False(a.Revoked);
        Assert.Single(a.References);
    }

    [Theory]
    [InlineData("critical", AdvisorySeverity.Critical)]
    [InlineData("HIGH", AdvisorySeverity.High)]
    [InlineData("Medium", AdvisorySeverity.Medium)]
    [InlineData("low", AdvisorySeverity.Low)]
    public void Severity_parsing_is_case_insensitive(string raw, AdvisorySeverity expected)
    {
        var json = Valid.Replace("\"severity\": \"high\"", $"\"severity\": \"{raw}\"");
        Assert.True(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(json), out var a, out _));
        Assert.Equal(expected, a!.Severity);
    }

    [Fact]
    public void Rejects_unknown_severity_rather_than_defaulting()
    {
        // An unrecognised severity must fail closed. Defaulting to Low would silently
        // downgrade a critical advisory; defaulting to Critical would let a typo act.
        var json = Valid.Replace("\"severity\": \"high\"", "\"severity\": \"spicy\"");
        Assert.False(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var err));
        Assert.Contains("severity", err!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"id\": \"2026-08-25-electrum-xss\",", "", "id")]
    [InlineData("\"identifier\": \"BTCPayServer.Plugins.Electrum\",", "", "identifier")]
    [InlineData("\"affectedVersions\": \">=1.0.0 && <1.2.3\",", "", "affectedVersions")]
    public void Rejects_missing_required_fields(string fragment, string replacement, string expectedField)
    {
        var json = Valid.Replace(fragment, replacement);
        Assert.False(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var err));
        // Exact match, not Contains: "id" is a substring of "identifier", so a loose
        // Contains("id") would still pass if a regression blamed the wrong field.
        Assert.Equal($"Missing required field: {expectedField}", err);
    }

    [Fact]
    public void Rejects_syntactically_invalid_json_without_throwing()
    {
        Assert.False(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes("{ not json"), out _, out var err));
        Assert.StartsWith("Malformed advisory JSON", err);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("7")]
    [InlineData("-1")]
    public void Rejects_numeric_severity_strings(string raw)
    {
        // The advisory schema is string-names-only. Enum.TryParse also accepts the
        // underlying numeric value (e.g. "2" -> High), which is undocumented surface -
        // in-range numerics must be rejected just like out-of-range ones.
        var json = Valid.Replace("\"severity\": \"high\"", $"\"severity\": \"{raw}\"");
        Assert.False(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(json), out _, out var err));
        Assert.Contains("severity", err!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("[{\"a\":1}]")]
    [InlineData("[true]")]
    [InlineData("[null]")]
    public void Non_string_reference_entries_are_dropped_without_throwing(string referencesArray)
    {
        // JsonElement.GetString() throws InvalidOperationException for any ValueKind other
        // than String or Null. These arrays are structurally valid JSON, so this must not
        // throw - it must drop the non-string entries and still return a usable advisory.
        var json = Valid.Replace("[\"https://example.com/a\"]", referencesArray);
        Assert.True(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(json), out var a, out var err));
        Assert.Empty(a!.References);
    }

    [Fact]
    public void Mixed_reference_array_keeps_only_valid_strings()
    {
        var json = Valid.Replace("[\"https://example.com/a\"]", "[\"https://ok\", 1, \"https://also-ok\"]");
        Assert.True(AdvisoryParser.TryParse(Encoding.UTF8.GetBytes(json), out var a, out var err));
        Assert.Equal(["https://ok", "https://also-ok"], a!.References);
    }

    [Fact]
    public void Parses_the_index()
    {
        var json = """
        [{"id":"a","path":"advisories/a","contentHash":"deadbeef"},
         {"id":"b","path":"advisories/b","contentHash":"cafe"}]
        """;
        var entries = AdvisoryParser.ParseIndex(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, entries.Length);
        Assert.Equal("deadbeef", entries[0].ContentHash);
        Assert.Equal("advisories/b", entries[1].Path);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    public void ParseIndex_returns_empty_array_on_malformed_input_rather_than_throwing(string json)
    {
        var entries = AdvisoryParser.ParseIndex(Encoding.UTF8.GetBytes(json));
        Assert.Empty(entries);
    }
}
