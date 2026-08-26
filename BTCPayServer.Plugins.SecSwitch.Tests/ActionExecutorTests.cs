using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public sealed class RecordingSink : IActionSink
{
    public List<string> Calls { get; } = [];
    public void QueueDisable(string identifier) => Calls.Add($"disable:{identifier}");
    public Task QueueUpdateAsync(string identifier, string version)
    { Calls.Add($"update:{identifier}:{version}"); return Task.CompletedTask; }
    public Task TriggerCoreUpdateAsync() { Calls.Add("core-update"); return Task.CompletedTask; }
    public void StopApplication() => Calls.Add("stop");
}

public class ActionExecutorTests
{
    static Advisory Adv(string identifier = "Plug", string? fixedVersion = "2.0.0") =>
        new() { Id = "x", Identifier = identifier, FixedVersion = fixedVersion, Title = "t" };

    static (ActionExecutor, RecordingSink) Make()
    {
        var sink = new RecordingSink();
        return (new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance), sink);
    }

    [Fact]
    public async Task None_and_notify_never_touch_the_instance()
    {
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.None, Adv());
        await exec.ExecuteAsync(SecSwitchAction.Notify, Adv());
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task Disable_queues_then_stops_for_restart()
    {
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());
        Assert.Equal(["disable:Plug", "stop"], sink.Calls);
    }

    [Fact]
    public async Task Update_downloads_and_queues_then_stops_for_restart()
    {
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv());
        Assert.Equal(["update:Plug:2.0.0", "stop"], sink.Calls);
    }

    [Fact]
    public async Task Update_without_a_fixed_version_falls_back_to_disable()
    {
        // Never call the update path with a null version — it would throw inside PluginService.
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(fixedVersion: null));
        Assert.Equal(["disable:Plug", "stop"], sink.Calls);
    }

    [Fact]
    public async Task Core_update_triggers_ssh_and_does_not_stop_the_process()
    {
        // btcpay-update.sh restarts the stack itself; stopping here would race it.
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.UpdateCore, Adv("BTCPayServer"));
        Assert.Equal(["core-update"], sink.Calls);
    }

    [Fact]
    public async Task Core_shutdown_stops_the_process_without_queueing_anything()
    {
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.ShutdownCore, Adv("BTCPayServer", fixedVersion: null));
        Assert.Equal(["stop"], sink.Calls);
    }

    [Fact]
    public async Task Sink_failure_is_reported_not_thrown()
    {
        var exec = new ActionExecutor(new ThrowingSink(), NullLogger<ActionExecutor>.Instance);
        var outcome = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    sealed class ThrowingSink : IActionSink
    {
        public void QueueDisable(string identifier) => throw new InvalidOperationException("boom");
        public Task QueueUpdateAsync(string identifier, string version) => throw new InvalidOperationException("boom");
        public Task TriggerCoreUpdateAsync() => throw new InvalidOperationException("boom");
        public void StopApplication() => throw new InvalidOperationException("boom");
    }

    // --- Hardening beyond the brief: ExecuteAsync must fail closed for every hostile or malformed
    // input, and a plugin identifier must never reach a destructive call unless it is a plain name. ---

    [Fact]
    public async Task Null_advisory_is_refused_not_disabled()
    {
        var (exec, sink) = Make();
        var outcome = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, null!);
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_advisory_is_refused_not_updated()
    {
        var (exec, sink) = Make();
        var outcome = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, null!);
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_identifier_is_refused_not_disabled()
    {
        var (exec, sink) = Make();
        var advisory = new Advisory { Id = "x", Identifier = null!, FixedVersion = "2.0.0", Title = "t" };
        var outcome = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, advisory);
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Blank_identifier_is_refused_not_disabled()
    {
        var (exec, sink) = Make();
        var outcome = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv(identifier: "   "));
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\..\\Windows")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("foo:bar")]
    [InlineData("..")]
    public async Task Path_traversal_or_separator_identifiers_are_refused_not_disabled(string hostileIdentifier)
    {
        var (exec, sink) = Make();
        var outcome = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv(identifier: hostileIdentifier));
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Path_traversal_identifier_is_refused_not_updated()
    {
        var (exec, sink) = Make();
        var outcome = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(identifier: "../evil", fixedVersion: "1.0.0"));
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_action_value_is_refused_and_never_throws()
    {
        var (exec, sink) = Make();
        var outcome = await exec.ExecuteAsync((SecSwitchAction)99, Adv());
        Assert.Empty(sink.Calls);
        Assert.False(string.IsNullOrWhiteSpace(outcome));
    }

    [Fact]
    public async Task Core_update_with_null_advisory_still_triggers_ssh()
    {
        // UpdateCore/ShutdownCore never consume the identifier, so a missing advisory should not
        // block them the way it blocks the plugin-identifier actions.
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.UpdateCore, null!);
        Assert.Equal(["core-update"], sink.Calls);
    }

    [Fact]
    public async Task Core_shutdown_with_null_advisory_still_stops()
    {
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.ShutdownCore, null!);
        Assert.Equal(["stop"], sink.Calls);
    }
}
