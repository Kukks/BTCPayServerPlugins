using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class PeriodicTaskTests
{
    [Fact]
    public void Build_state_maps_installed_plugins_and_core_version()
    {
        var state = SecSwitchPeriodicTask.BuildState(
            [new StubPlugin("Alpha", new Version(1, 2, 3))], "2.4.2", canUseSsh: true);

        Assert.Equal(new Version(1, 2, 3), state.InstalledPlugins["Alpha"]);
        Assert.Equal(new Version(2, 4, 2), state.CoreVersion);
        Assert.True(state.CanUseSsh);
    }

    [Theory]
    [InlineData("v2.4.2", "2.4.2")]
    [InlineData("2.4.2+abc123", "2.4.2")]
    [InlineData("v2.4.2+abc123", "2.4.2")]
    public void Core_version_strips_prefix_and_build_metadata(string raw, string expected)
    {
        // BTCPayServerEnvironment.Version carries a leading 'v' and a +buildmeta suffix that
        // System.Version cannot parse; PluginService.GetShortBtcpayVersion strips them the same way
        // (confirmed by reading BTCPayServer/Plugins/PluginManager/PluginService.cs:61 - this
        // mirrors that exact expression rather than merely resembling it).
        var state = SecSwitchPeriodicTask.BuildState([], raw, canUseSsh: false);
        Assert.Equal(Version.Parse(expected), state.CoreVersion);
    }

    [Fact]
    public void Unparseable_core_version_falls_back_without_throwing()
    {
        var state = SecSwitchPeriodicTask.BuildState([], "not-a-version", canUseSsh: false);
        Assert.NotNull(state.CoreVersion);
    }

    [Fact]
    public void Null_plugin_list_falls_back_to_an_empty_map_without_throwing()
    {
        // BuildState's parameter is typed non-nullable, but that is a compile-time hint only - a
        // caller across an assembly boundary can still pass null (matches this codebase's pervasive
        // "defensive guards mirror the type but do not trust it" posture - see e.g.
        // AdvisoryVerifier.Verify's own doc comment). IEnumerable<IBTCPayServerPlugin> is what core's
        // own DI hands SecSwitchPeriodicTask's constructor; an empty registration set is a real,
        // reachable input shape even without a hostile caller.
        var state = SecSwitchPeriodicTask.BuildState(null!, "2.4.2", canUseSsh: false);
        Assert.Empty(state.InstalledPlugins);
    }

    [Fact]
    public void Null_element_in_plugin_list_is_skipped_without_throwing()
    {
        var state = SecSwitchPeriodicTask.BuildState(
            [null!, new StubPlugin("Alpha", new Version(1, 0, 0))], "2.4.2", canUseSsh: false);

        Assert.Single(state.InstalledPlugins);
        Assert.Equal(new Version(1, 0, 0), state.InstalledPlugins["Alpha"]);
    }

    sealed class StubPlugin(string id, Version version) : BTCPayServer.Abstractions.Models.BaseBTCPayServerPlugin
    {
        public override string Identifier => id;
        public override Version Version => version;
    }
}
