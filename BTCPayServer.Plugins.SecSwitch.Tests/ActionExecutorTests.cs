using BTCPayServer.Configuration;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static BTCPayServer.Plugins.SecSwitch.Tests.Fixtures;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class ActionExecutorTests
{
    readonly FakeSink _sink = new();

    ActionExecutor Executor() => new(_sink, NullLogger<ActionExecutor>.Instance);

    [Theory]
    [InlineData(SecSwitchAction.None)]
    [InlineData(SecSwitchAction.Notify)]
    [InlineData(SecSwitchAction.Lockdown)]
    public async Task Actions_without_an_instance_change_touch_nothing(SecSwitchAction action)
    {
        var outcome = await Executor().ExecuteAsync(action, Adv());

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.RestartNeeded);
        Assert.Empty(_sink.Disabled);
        Assert.Empty(_sink.Updated);
        Assert.Equal(0, _sink.CoreUpdates);
    }

    [Fact]
    public async Task Disable_queues_and_asks_for_a_restart_without_stopping_by_itself()
    {
        var outcome = await Executor().ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.RestartNeeded);
        Assert.Equal(["Plug"], _sink.Disabled);
        Assert.Equal(0, _sink.Stops);
    }

    [Fact]
    public async Task Disable_that_cannot_be_queued_is_a_failure_without_a_restart()
    {
        _sink.QueueDisableResult = false;

        var outcome = await Executor().ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.RestartNeeded);
    }

    [Fact]
    public async Task Update_queues_the_fixed_version_and_asks_for_a_restart()
    {
        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(fixedVersion: "1.2.3"));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.RestartNeeded);
        Assert.Equal([("Plug", "1.2.3")], _sink.Updated);
        Assert.Empty(_sink.Disabled);
    }

    [Fact]
    public async Task Update_without_a_fixed_version_disables()
    {
        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv());

        Assert.True(outcome.Succeeded);
        Assert.Empty(_sink.Updated);
        Assert.Equal(["Plug"], _sink.Disabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_update_falls_back_to_disable(bool throws)
    {
        _sink.QueueUpdateResult = false;
        _sink.QueueUpdateThrows = throws ? new InvalidOperationException("not compatible") : null;

        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(fixedVersion: "1.2.3"));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.RestartNeeded);
        Assert.Equal(["Plug"], _sink.Disabled);
        Assert.Contains("Disabling instead", outcome.Message);
    }

    [Fact]
    public async Task A_failed_update_and_a_failed_disable_is_a_failure()
    {
        _sink.QueueUpdateResult = false;
        _sink.QueueDisableResult = false;

        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(fixedVersion: "1.2.3"));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.RestartNeeded);
    }

    [Fact]
    public async Task A_routine_update_never_falls_back_to_disable()
    {
        _sink.QueueUpdateResult = false;

        var outcome = await Executor().UpdatePluginAsync("Plug", "1.2.3");

        Assert.False(outcome.Succeeded);
        Assert.Empty(_sink.Disabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.3")]
    public async Task An_advisory_update_leaves_the_plugin_enabled_when_disable_fallback_is_off(string? fixedVersion)
    {
        _sink.QueueUpdateResult = false;
        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(fixedVersion: fixedVersion),
            allowDisableFallback: false);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.RestartNeeded);
        Assert.Empty(_sink.Disabled);
    }

    [Fact]
    public async Task Core_update_triggers_the_host_update_and_needs_no_restart()
    {
        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdateCore, Adv(identifier: "BTCPayServer"));

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.RestartNeeded);
        Assert.Equal(1, _sink.CoreUpdates);
    }

    [Fact]
    public async Task Core_update_failure_is_reported_not_thrown()
    {
        _sink.CoreUpdateThrows = new InvalidOperationException("host update failed");

        var outcome = await Executor().ExecuteAsync(SecSwitchAction.UpdateCore, Adv(identifier: "BTCPayServer"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("host update failed", outcome.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a..b")]
    [InlineData("a b")]
    [InlineData("a;b")]
    public async Task Unsafe_plugin_identifiers_are_refused_before_reaching_the_sink(string identifier)
    {
        var disable = await Executor().ExecuteAsync(SecSwitchAction.DisablePlugin, Adv(identifier: identifier));
        var update = await Executor().ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(identifier: identifier, fixedVersion: "1.2.3"));

        Assert.False(disable.Succeeded);
        Assert.False(update.Succeeded);
        Assert.Empty(_sink.Disabled);
        Assert.Empty(_sink.Updated);
    }

    [Fact]
    public async Task An_identifier_over_the_length_cap_is_refused()
    {
        var outcome = await Executor().ExecuteAsync(SecSwitchAction.DisablePlugin, Adv(identifier: new string('a', 129)));

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task An_unknown_action_is_refused_without_throwing()
    {
        var outcome = await Executor().ExecuteAsync((SecSwitchAction)999, Adv());

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void RequestRestart_stops_the_application()
    {
        Executor().RequestRestart();

        Assert.Equal(1, _sink.Stops);
    }

    [Fact]
    public void Sanitize_strips_control_and_line_separator_characters_and_truncates()
    {
        var separators = $"{(char)0x2028}{(char)0x2029}";

        Assert.Equal("abc", ActionExecutor.Sanitize("a\nb\r\tc"));
        Assert.Equal("xyz", ActionExecutor.Sanitize($"x{separators[0]}y{separators[1]}z"));
        Assert.Equal(new string('a', 200) + "...", ActionExecutor.Sanitize(new string('a', 300)));
        Assert.Equal("", ActionExecutor.Sanitize(null));
    }

    [Fact]
    public void Identifiers_resolve_to_the_on_disk_casing()
    {
        using var dir = new TempPluginDir("BTCPayServer.Plugins.Prism");

        Assert.True(BtcPayActionSink.TryResolveInstalledDirectory(dir.Path, "btcpayserver.plugins.prism", out var name, out var ambiguous));
        Assert.Equal("BTCPayServer.Plugins.Prism", name);
        Assert.Empty(ambiguous);
    }

    [Fact]
    public void Unknown_or_missing_plugin_directories_do_not_resolve()
    {
        using var dir = new TempPluginDir("BTCPayServer.Plugins.Prism");
        var missing = Path.Combine(Path.GetTempPath(), "secswitch-missing-" + Guid.NewGuid());

        Assert.False(BtcPayActionSink.TryResolveInstalledDirectory(dir.Path, "SomeOtherPlugin", out _, out _));
        Assert.False(BtcPayActionSink.TryResolveInstalledDirectory(missing, "AnyPlugin", out _, out _));
    }

    [Fact]
    public void An_exact_match_wins_over_case_variant_siblings()
    {
        string[] candidates = ["btcpayserver.plugins.prism", "BTCPayServer.Plugins.Prism"];

        Assert.True(BtcPayActionSink.TryResolveAmongCandidates(candidates, "BTCPayServer.Plugins.Prism", out var name, out _));
        Assert.Equal("BTCPayServer.Plugins.Prism", name);
    }

    [Fact]
    public void Ambiguous_case_variants_are_refused_rather_than_guessed()
    {
        string[] candidates = ["BTCPayServer.Plugins.Prism", "btcpayserver.plugins.prism"];

        Assert.False(BtcPayActionSink.TryResolveAmongCandidates(candidates, "BTCPAYSERVER.PLUGINS.PRISM", out _, out var ambiguous));
        Assert.Equal(2, ambiguous.Count);
    }

    [Fact]
    public void The_sink_resolves_PluginService_from_a_scope_not_a_captured_instance()
    {
        var parameters = typeof(BtcPayActionSink).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(IServiceScopeFactory), parameters);
        Assert.DoesNotContain(typeof(BTCPayServer.Plugins.PluginService), parameters);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("enable")]
    public async Task An_update_leaves_pending_plugin_changes_alone(string pendingOperation)
    {
        using var dir = new TempPluginDir("Plug");
        PluginManager.QueueCommands(dir.Path, (pendingOperation, "plug"));
        var sink = SinkForPluginDirectory(dir.Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sink.QueueUpdateAsync("Plug", "1.2.3"));

        Assert.Contains("already queued", error.Message);
        Assert.Equal((pendingOperation, "plug"), Assert.Single(PluginManager.GetPendingCommands(dir.Path)));
    }

    [Theory]
    [InlineData("disable", "disable")]
    [InlineData("delete", "delete")]
    [InlineData("install", "install", "disable")]
    public void Disabling_a_plugin_keeps_any_existing_pending_operation(string pendingOperation, params string[] commands)
    {
        using var dir = new TempPluginDir("Plug");
        PluginManager.QueueCommands(dir.Path, (pendingOperation, "Plug"));

        Assert.True(SinkForPluginDirectory(dir.Path).QueueDisable("Plug"));
        Assert.Equal(commands, PluginManager.GetPendingCommands(dir.Path).Select(command => command.command));
    }

    [Fact]
    public void Automatic_updates_require_compatible_loaded_dependencies_without_pending_changes()
    {
        var manifest = new PluginService.AvailablePlugin
        {
            Dependencies =
            [
                new() { Identifier = "BTCPayServer", Condition = ">=2.4.7" },
                new() { Identifier = "Dependency", Condition = ">=1.2.3" }
            ]
        };
        var installed = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCPayServer"] = new(2, 4, 7),
            ["Dependency"] = new(1, 2, 3)
        };

        Assert.True(BtcPayActionSink.CanInstallUpdate(manifest, installed, []));
        Assert.False(BtcPayActionSink.CanInstallUpdate(manifest, installed, ["dependency"]));
        installed["Dependency"] = new(1, 2, 2);
        Assert.False(BtcPayActionSink.CanInstallUpdate(manifest, installed, []));
        installed.Remove("Dependency");
        Assert.False(BtcPayActionSink.CanInstallUpdate(manifest, installed, []));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SecSwitch_stages_updates_without_replacing_an_admin_download(bool adminDownload, bool adminQueued)
    {
        using var dir = new TempPluginDir("Plug");
        const string published = """{"manifestInfo":{"identifier":"Plug","version":"1.2.3.0"}}""";
        var payloadPath = Path.Combine(dir.Path, "Plug.btcpay");
        var manifestPath = Path.Combine(dir.Path, "Plug.json");
        using var client = new HttpClient(new StubPluginBuilder(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/plugins/Plug")
                return $"[{published}]";
            if (!path.EndsWith("/download", StringComparison.Ordinal))
                return published;
            if (adminDownload)
            {
                File.WriteAllText(payloadPath, "admin package");
                File.WriteAllText(manifestPath, "admin manifest");
                if (adminQueued)
                    PluginManager.QueueCommands(dir.Path, ("install", "Plug"));
            }
            return "SecSwitch package";
        })) { BaseAddress = new Uri("https://plugins.example/") };
        var networks = new BTCPayNetworkProvider([], new NBXplorer.NBXplorerNetworkProvider(NBitcoin.ChainName.Regtest),
            new Logging.Logs());
        using var services = new ServiceCollection()
            .AddSingleton(new PluginBuilderClient(client))
            .AddSingleton(new PoliciesSettings())
            .AddSingleton(new BTCPayServerEnvironment(null!, networks, null!, new BTCPayServerOptions()))
            .BuildServiceProvider();
        var sink = SinkForPluginDirectory(dir.Path, services.GetRequiredService<IServiceScopeFactory>());

        if (adminQueued)
            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.QueueUpdateAsync("Plug", "1.2.3"));
        else if (adminDownload)
            await Assert.ThrowsAsync<IOException>(() => sink.QueueUpdateAsync("Plug", "1.2.3"));
        else
            Assert.True(await sink.QueueUpdateAsync("Plug", "1.2.3"));

        Assert.Equal(adminDownload ? "admin package" : "SecSwitch package", File.ReadAllText(payloadPath));
        if (adminDownload)
            Assert.Equal("admin manifest", File.ReadAllText(manifestPath));
        Assert.Equal(adminDownload && !adminQueued ? [] : [("install", "Plug")], PluginManager.GetPendingCommands(dir.Path));
        Assert.Equal("Plug", Path.GetFileName(Assert.Single(Directory.GetDirectories(dir.Path))));
    }

    sealed class StubPluginBuilder(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body(request)) });
    }

    static BtcPayActionSink SinkForPluginDirectory(string pluginDirectory, IServiceScopeFactory? scopeFactory = null) =>
        new(scopeFactory!, null!, Options.Create(new DataDirectories { PluginDir = pluginDirectory }),
            null!, null!, null!, null!, NullLogger<BtcPayActionSink>.Instance);

    sealed class TempPluginDir : IDisposable
    {
        readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("secswitch-test-");

        public TempPluginDir(params string[] plugins)
        {
            foreach (var plugin in plugins)
                Directory.CreateDirectory(System.IO.Path.Combine(_dir.FullName, plugin));
        }

        public string Path => _dir.FullName;

        public void Dispose() => _dir.Delete(recursive: true);
    }
}
