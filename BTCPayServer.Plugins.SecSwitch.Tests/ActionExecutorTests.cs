using BTCPayServer.Plugins;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public sealed class RecordingSink : IActionSink
{
    public List<string> Calls { get; } = [];
    public bool QueueDisable(string identifier) { Calls.Add($"disable:{identifier}"); return true; }
    public Task<bool> QueueUpdateAsync(string identifier, string version)
    { Calls.Add($"update:{identifier}:{version}"); return Task.FromResult(true); }
    public Task TriggerCoreUpdateAsync() { Calls.Add("core-update"); return Task.CompletedTask; }
    public void StopApplication() => Calls.Add("stop");
}

// Final whole-branch review, Finding C1 (Critical): ExecuteAsync returns (bool Succeeded, string
// Outcome), not a bare string. The prose alone was never a usable signal - "Queued disable of X..."
// and "Failed to queue disable of X..." are the same type - and its only caller recorded both under
// the terminal LedgerStatus.Acted, so a failed action was latched out of every future poll, had its
// content hash cached against re-download, and was announced to the admin as "Handled". Every
// Assert.False(succeeded) below therefore pins a load-bearing fact, not a redundant one: without it
// the flag could silently regress to a constant `true` with every string assertion in this file
// still green. Success_flag_is_true_for_every_path_that_actually_acted pins the other direction.
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
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    sealed class ThrowingSink : IActionSink
    {
        public bool QueueDisable(string identifier) => throw new InvalidOperationException("boom");
        public Task<bool> QueueUpdateAsync(string identifier, string version) => throw new InvalidOperationException("boom");
        public Task TriggerCoreUpdateAsync() => throw new InvalidOperationException("boom");
        public void StopApplication() => throw new InvalidOperationException("boom");
    }

    // A sink whose async members fault via Task.FromException instead of throwing synchronously,
    // and whose StopApplication records rather than throws - so a test can assert it was never
    // reached, not just that some exception happened to surface.
    sealed class AsyncFaultingSink : IActionSink
    {
        public List<string> Calls { get; } = [];
        public bool QueueDisable(string identifier) => throw new InvalidOperationException("boom");
        public Task<bool> QueueUpdateAsync(string identifier, string version) =>
            Task.FromException<bool>(new InvalidOperationException("boom"));
        public Task TriggerCoreUpdateAsync() => Task.FromException(new InvalidOperationException("boom"));
        public void StopApplication() => Calls.Add("stop");
    }

    // Simulates BtcPayActionSink failing to resolve the identifier to an installed plugin directory:
    // reports failure via the return value, without throwing and without queueing anything.
    sealed class UnresolvableSink : IActionSink
    {
        public List<string> Calls { get; } = [];
        public bool QueueDisable(string identifier) { Calls.Add($"disable-attempt:{identifier}"); return false; }
        public Task<bool> QueueUpdateAsync(string identifier, string version)
        { Calls.Add($"update-attempt:{identifier}:{version}"); return Task.FromResult(false); }
        public Task TriggerCoreUpdateAsync() => throw new NotSupportedException();
        public void StopApplication() => Calls.Add("stop");
    }

    // --- Hardening beyond the brief: ExecuteAsync must fail closed for every hostile or malformed
    // input, and a plugin identifier must never reach a destructive call unless it is a plain name. ---

    [Fact]
    public async Task Null_advisory_is_refused_not_disabled()
    {
        var (exec, sink) = Make();
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, null);
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_advisory_is_refused_not_updated()
    {
        var (exec, sink) = Make();
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, null);
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_identifier_is_refused_not_disabled()
    {
        var (exec, sink) = Make();
        var advisory = new Advisory { Id = "x", Identifier = null!, FixedVersion = "2.0.0", Title = "t" };
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, advisory);
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Blank_identifier_is_refused_not_disabled()
    {
        var (exec, sink) = Make();
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv(identifier: "   "));
        Assert.False(succeeded); // Finding C1 - see the note above this class.
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
    [InlineData("foo*bar")]
    [InlineData("foo?bar")]
    [InlineData("foo\u2028bar")]
    [InlineData("foo\u2029bar")]
    [InlineData("Plug\n")] // .NET regex '$' (without Multiline) matches before a trailing '\n'; \A/\z must not.
    public async Task Path_traversal_or_separator_identifiers_are_refused_not_disabled(string hostileIdentifier)
    {
        var (exec, sink) = Make();
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv(identifier: hostileIdentifier));
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Path_traversal_identifier_is_refused_not_updated()
    {
        var (exec, sink) = Make();
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(identifier: "../evil", fixedVersion: "1.0.0"));
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Empty(sink.Calls);
        Assert.Contains("unsafe", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_action_value_is_refused_and_never_throws()
    {
        var (exec, sink) = Make();
        var (succeeded, outcome) = await exec.ExecuteAsync((SecSwitchAction)99, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Empty(sink.Calls);
        Assert.False(string.IsNullOrWhiteSpace(outcome));
    }

    [Fact]
    public async Task Core_update_with_null_advisory_still_triggers_ssh()
    {
        // UpdateCore/ShutdownCore never consume the identifier, so a missing advisory should not
        // block them the way it blocks the plugin-identifier actions.
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.UpdateCore, null);
        Assert.Equal(["core-update"], sink.Calls);
    }

    [Fact]
    public async Task Core_shutdown_with_null_advisory_still_stops()
    {
        var (exec, sink) = Make();
        await exec.ExecuteAsync(SecSwitchAction.ShutdownCore, null);
        Assert.Equal(["stop"], sink.Calls);
    }

    // --- Review round 2: a queue step can now report "nothing was actually queued" (an identifier
    // that doesn't resolve to an installed plugin, case-insensitively or otherwise), and the
    // application must not be stopped when that happens - stopping on a no-op queue is an outage
    // for nothing, worse than doing nothing at all. Covers both failure shapes a sink can produce:
    // a synchronous throw, and a Task that completes faulted (Task.FromException). ---

    [Fact]
    public async Task Disable_queue_failure_does_not_stop_the_application()
    {
        var sink = new UnresolvableSink();
        var exec = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.DoesNotContain("stop", sink.Calls);
        // "Plug" alone would also match the success message ("Queued disable of Plug; ..."), so this
        // asserts on wording only the refusal path produces.
        Assert.Contains("Failed to queue", outcome);
    }

    [Fact]
    public async Task Update_queue_failure_does_not_stop_the_application()
    {
        var sink = new UnresolvableSink();
        var exec = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.DoesNotContain("stop", sink.Calls);
        Assert.Contains("Failed to queue", outcome);
    }

    [Fact]
    public async Task Disable_queue_throwing_synchronously_does_not_stop_the_application()
    {
        var sink = new AsyncFaultingSink();
        var exec = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.DisablePlugin, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.DoesNotContain("stop", sink.Calls);
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_queue_failure_via_faulted_task_does_not_stop_the_application()
    {
        var sink = new AsyncFaultingSink();
        var exec = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.DoesNotContain("stop", sink.Calls);
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_queue_throwing_synchronously_is_reported_not_thrown()
    {
        var exec = new ActionExecutor(new ThrowingSink(), NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv());
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Core_update_failure_is_reported_not_thrown()
    {
        var exec = new ActionExecutor(new ThrowingSink(), NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdateCore, Adv("BTCPayServer"));
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Core_update_failure_via_faulted_task_does_not_stop_and_is_reported()
    {
        var sink = new AsyncFaultingSink();
        var exec = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdateCore, Adv("BTCPayServer"));
        Assert.False(succeeded); // Finding C1 - see the note above this class.
        Assert.DoesNotContain("stop", sink.Calls);
        Assert.Contains("failed", outcome, StringComparison.OrdinalIgnoreCase);
    }

    // --- Finding C1, the other direction: a flag that is always false would pass every
    // Assert.False above while permanently recording genuine actions as NeedsAttention - which would
    // re-run a shutdown or a disable on every single poll, the exact action loop the ledger exists to
    // prevent. Both directions have to be pinned. ---

    [Theory]
    [InlineData(SecSwitchAction.None)]
    [InlineData(SecSwitchAction.Notify)]
    [InlineData(SecSwitchAction.DisablePlugin)]
    [InlineData(SecSwitchAction.UpdatePlugin)]
    [InlineData(SecSwitchAction.UpdateCore)]
    [InlineData(SecSwitchAction.ShutdownCore)]
    public async Task Success_flag_is_true_for_every_path_that_actually_acted(SecSwitchAction action)
    {
        var (exec, _) = Make(); // RecordingSink queues successfully and never throws
        var (succeeded, outcome) = await exec.ExecuteAsync(action, Adv());
        Assert.True(succeeded);
        Assert.False(string.IsNullOrWhiteSpace(outcome));
    }

    [Fact]
    public async Task Update_without_a_fixed_version_reports_the_disable_fallbacks_own_success_flag()
    {
        // The UpdatePlugin -> Disable fallback must propagate Disable's result, not overwrite it: an
        // unresolvable identifier that falls back to a disable which then queues nothing has to stay
        // false all the way out, or the fallback becomes a hole in the Finding C1 fix.
        var sink = new UnresolvableSink();
        var exec = new ActionExecutor(sink, NullLogger<ActionExecutor>.Instance);
        var (succeeded, outcome) = await exec.ExecuteAsync(SecSwitchAction.UpdatePlugin, Adv(fixedVersion: null));
        Assert.False(succeeded);
        Assert.Contains("Failed to queue disable", outcome);
        Assert.DoesNotContain("stop", sink.Calls);
    }
}

// Resolution is what makes the case-mismatch fix actually work: it is exercised against a real
// (temporary) directory rather than through RecordingSink, because the resolution logic lives
// entirely inside BtcPayActionSink - RecordingSink replaces that class outright in the tests above,
// so it cannot observe what BtcPayActionSink itself does with an identifier before queueing.
public class BtcPayActionSinkResolutionTests
{
    [Fact]
    public void Resolves_identifier_differing_only_in_case_to_the_canonical_on_disk_name()
    {
        var dir = Directory.CreateTempSubdirectory("secswitch-test-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Prism"));

            var resolved = BtcPayActionSink.TryResolveInstalledDirectory(
                dir.FullName, "btcpayserver.plugins.prism", out var name, out _);

            Assert.True(resolved);
            Assert.Equal("BTCPayServer.Plugins.Prism", name);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Exact_case_match_resolves_to_itself()
    {
        var dir = Directory.CreateTempSubdirectory("secswitch-test-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Prism"));

            var resolved = BtcPayActionSink.TryResolveInstalledDirectory(
                dir.FullName, "BTCPayServer.Plugins.Prism", out var name, out _);

            Assert.True(resolved);
            Assert.Equal("BTCPayServer.Plugins.Prism", name);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void No_matching_directory_fails_to_resolve()
    {
        var dir = Directory.CreateTempSubdirectory("secswitch-test-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Prism"));

            var resolved = BtcPayActionSink.TryResolveInstalledDirectory(
                dir.FullName, "SomeOtherPlugin", out _, out _);

            Assert.False(resolved);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_plugin_directory_fails_to_resolve_without_throwing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "secswitch-does-not-exist-" + Guid.NewGuid());

        var resolved = BtcPayActionSink.TryResolveInstalledDirectory(missing, "AnyPlugin", out _, out _);

        Assert.False(resolved);
    }

    // --- Review round 2, Finding B: enumeration order is not a safe way to pick between two
    // directories that differ only by case - a case-sensitive filesystem (production Linux) can
    // genuinely have both, and the update path itself can create such a sibling (PluginManager's
    // "install" replay has no Directory.Exists gate). An exact match must always win, and with no
    // exact match, two-or-more case-insensitive candidates must refuse to guess.
    //
    // These exercise TryResolveAmongCandidates directly, with an explicit list of names, rather than
    // creating two real directories differing only by case: this codebase's own dev/CI environment is
    // Windows, where NTFS is case-insensitive by default, so two such directories cannot be reliably
    // created side by side on a real temp directory here even though they can - and do - coexist on
    // the case-sensitive Linux filesystems SecSwitch actually targets in production. ---

    [Fact]
    public void Exact_match_is_preferred_when_a_case_variant_sibling_exists()
    {
        string[] candidates = ["btcpayserver.plugins.prism", "BTCPayServer.Plugins.Prism"];

        var resolved = BtcPayActionSink.TryResolveAmongCandidates(
            candidates, "BTCPayServer.Plugins.Prism", out var name, out var ambiguous);

        Assert.True(resolved);
        Assert.Equal("BTCPayServer.Plugins.Prism", name);
        Assert.Empty(ambiguous);
    }

    [Fact]
    public void Ambiguous_case_variants_with_no_exact_match_fail_to_resolve()
    {
        string[] candidates = ["BTCPayServer.Plugins.Prism", "btcpayserver.plugins.prism"];

        // Matches neither candidate exactly, but both case-insensitively.
        var resolved = BtcPayActionSink.TryResolveAmongCandidates(
            candidates, "BTCPAYSERVER.PLUGINS.PRISM", out _, out var ambiguous);

        Assert.False(resolved);
        Assert.Equal(2, ambiguous.Count);
        Assert.Contains("BTCPayServer.Plugins.Prism", ambiguous);
        Assert.Contains("btcpayserver.plugins.prism", ambiguous);
    }

    [Fact]
    public void Single_case_insensitive_match_still_resolves_via_the_disk_backed_overload()
    {
        // Confirms TryResolveInstalledDirectory's own filesystem plumbing (not just the pure
        // matching rule) still resolves the ordinary, non-ambiguous case correctly.
        var dir = Directory.CreateTempSubdirectory("secswitch-test-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Prism"));

            var resolved = BtcPayActionSink.TryResolveInstalledDirectory(
                dir.FullName, "btcpayserver.plugins.prism", out var name, out var ambiguous);

            Assert.True(resolved);
            Assert.Equal("BTCPayServer.Plugins.Prism", name);
            Assert.Empty(ambiguous);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // --- Task 12 review, Finding I1: BtcPayActionSink is registered as a singleton
    // (SecSwitchPlugin.cs: services.AddSingleton<IActionSink, BtcPayActionSink>()), but core
    // registers PluginService TRANSIENT (PluginManagerPlugin.cs) and PluginService itself owns a
    // typed HttpClient (PluginBuilderClient) plus a PoliciesSettings snapshot - a
    // constructor-injected PluginService here would be a captive dependency, frozen (along with its
    // HttpClient's connection pool/DNS resolution and its settings snapshot) for the life of the
    // process. NET's built-in scope validation does not catch a singleton depending on a transient,
    // so this is a structural regression guard: it proves the constructor no longer takes
    // PluginService directly, and does take an IServiceScopeFactory (see QueueUpdateAsync, which
    // resolves PluginService from a fresh, disposed-after-use scope on every call instead).
    //
    // PluginService's own real constructor needs a working BTCPayNetworkProvider/TorServices/
    // IWebHostEnvironment graph (see PluginService.cs and BTCPayServerEnvironment.cs) that is
    // impractical to construct in this focused plugin test project - consistent with this test
    // project never having exercised BtcPayActionSink's other PluginService-touching behaviour
    // (QueueUpdateAsync) end to end either, only its pure static resolution helpers above. This test
    // pins the constructor SHAPE the fix depends on, which is what a regression here would actually
    // break first (re-adding a `PluginService pluginService` constructor parameter compiles fine and
    // would otherwise go unnoticed).

    [Fact]
    public void Constructor_resolves_PluginService_from_a_scope_factory_not_a_captured_instance()
    {
        var ctor = typeof(BtcPayActionSink).GetConstructors().Single();
        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(IServiceScopeFactory), parameterTypes);
        Assert.DoesNotContain(typeof(PluginService), parameterTypes);
    }
}
