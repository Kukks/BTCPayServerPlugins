using BTCPayServer.Plugins.SecSwitch.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class PluginUpdateSelectionTests
{
    static PublishedVersion Published(string? manifestJson) => new()
    {
        ManifestInfo = manifestJson is null ? null : JObject.Parse(manifestJson)
    };

    static PublishedVersion Manifest(string identifier, string version) =>
        Published($$"""{"Identifier":"{{identifier}}","Version":"{{version}}"}""");

    static Dictionary<string, Version> Installed(string id = "Plug", string version = "1.0.0") =>
        new(StringComparer.OrdinalIgnoreCase) { [id] = Version.Parse(version) };

    [Fact]
    public void A_newer_published_version_is_an_update()
    {
        var updates = BtcPayActionSink.SelectUpdates([Manifest("Plug", "1.2.0")], Installed());

        var update = Assert.Single(updates);
        Assert.Equal("Plug", update.Identifier);
        Assert.Equal(new Version(1, 2, 0), update.Available);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("0.9.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0.0")]
    public void The_same_or_an_older_version_is_not_an_update_whatever_the_number_of_parts(string published, string installed)
    {
        Assert.Empty(BtcPayActionSink.SelectUpdates([Manifest("Plug", published)], Installed(version: installed)));
    }

    [Fact]
    public void The_highest_published_version_wins_per_plugin()
    {
        var updates = BtcPayActionSink.SelectUpdates([Manifest("Plug", "1.2.0"), Manifest("Plug", "1.4.0")], Installed());

        Assert.Equal(new Version(1, 4, 0), Assert.Single(updates).Available);
    }

    [Fact]
    public void Identifiers_match_case_insensitively()
    {
        Assert.Single(BtcPayActionSink.SelectUpdates([Manifest("PLUG", "1.2.0")], Installed("plug")));
    }

    [Fact]
    public void Plugins_with_a_pending_command_are_skipped_so_an_install_cannot_undo_a_queued_disable()
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PLUG" };

        Assert.Empty(BtcPayActionSink.SelectUpdates([Manifest("Plug", "1.2.0")], Installed(), pending));
    }

    [Fact]
    public void Plugins_that_are_not_installed_and_unusable_manifests_are_ignored()
    {
        var updates = BtcPayActionSink.SelectUpdates(
            [
                Manifest("Other", "9.0.0"),
                Published("""{"Version":"9.0.0"}"""),
                Published("""{"Identifier":"Plug"}"""),
                Published(null)
            ],
            Installed());

        Assert.Empty(updates);
    }

    [Theory]
    [InlineData("1.2.3", new[] { "1.2.3.0", "1.2.3" })]
    [InlineData("1.2.3.0", new[] { "1.2.3.0" })]
    [InlineData("1.2.3.4", new[] { "1.2.3.4" })]
    [InlineData("1.2", new[] { "1.2.0.0", "1.2" })]
    [InlineData("not a version", new string[0])]
    [InlineData("", new string[0])]
    public void Update_candidates_try_the_four_part_form_the_plugin_builder_publishes_first(string version, string[] expected)
    {
        Assert.Equal(expected, BtcPayActionSink.UpdateCandidates(version));
    }
}
