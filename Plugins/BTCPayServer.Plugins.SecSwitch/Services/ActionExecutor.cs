using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// The destructive primitives a <see cref="PolicyDecision"/> can trigger. Exists so
/// <see cref="ActionExecutor"/> can be tested without disabling a real plugin or stopping the
/// process - <see cref="BtcPayActionSink"/> is the only implementation that touches BTCPayServer.
/// <see cref="QueueDisable"/> and <see cref="QueueUpdateAsync"/> report whether anything was
/// actually queued: the identifier they are given may not resolve to anything the sink can act on
/// (see <see cref="BtcPayActionSink"/>'s remarks), and a caller must not stop the application on the
/// strength of a queue that never happened.
/// </summary>
public interface IActionSink
{
    bool QueueDisable(string identifier);
    Task<bool> QueueUpdateAsync(string identifier, string version);
    Task TriggerCoreUpdateAsync();
    void StopApplication();
}

/// <summary>
/// Carries out a policy decision against a live instance. This is the only place in SecSwitch that
/// contains the decision-to-primitive mapping; it never calls a BTCPayServer API directly (that all
/// lives behind <see cref="IActionSink"/>) and it never lets an exception - or a hostile/malformed
/// input - escape as a thrown exception. Every path returns a human-readable outcome instead, and
/// the application is only ever stopped after a queue step has confirmed it actually queued something.
/// </summary>
public sealed class ActionExecutor(IActionSink sink, ILogger<ActionExecutor> logger)
{
    // Real BTCPayServer plugin identifiers are dotted assembly-style names (e.g.
    // "BTCPayServer.Plugins.Foo"). An allowlist of the characters such a name can ever legitimately
    // contain is strictly safer than trying to enumerate every hazardous character by hand: a
    // blocklist only closes the gaps its author thought of, and misses things like a literal star or
    // question mark, Windows reserved device names, trailing dots/spaces, and the Unicode line and
    // paragraph separators U+2028/U+2029 (NOT in the Unicode "control" category, so char.IsControl
    // alone does not catch them). The length cap matches what any real catalog identifier looks like.
    private static readonly Regex SafeIdentifierPattern = new("^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    public async Task<string> ExecuteAsync(SecSwitchAction action, Advisory? advisory)
    {
        try
        {
            switch (action)
            {
                case SecSwitchAction.None:
                case SecSwitchAction.Notify:
                    return "No instance change made.";

                case SecSwitchAction.DisablePlugin:
                    return Disable(advisory);

                case SecSwitchAction.UpdatePlugin:
                    // Never call the update path with a null/blank version - PluginService throws
                    // on it. With no fixed version, disabling is the only safe automatic action.
                    if (advisory is null || string.IsNullOrWhiteSpace(advisory.FixedVersion))
                        return Disable(advisory);
                    if (!IsSafeIdentifier(advisory.Identifier))
                        return $"Refusing to update: plugin identifier '{Sanitize(advisory.Identifier)}' is missing or unsafe.";
                    if (!await sink.QueueUpdateAsync(advisory.Identifier, advisory.FixedVersion))
                        return $"Failed to queue update of {advisory.Identifier}: no installed plugin matches that identifier.";
                    sink.StopApplication();
                    return $"Queued update of {advisory.Identifier} to {advisory.FixedVersion}; stopping for restart.";

                case SecSwitchAction.UpdateCore:
                    // btcpay-update.sh brings the stack down and up itself; do not also stop here,
                    // or we would race our own SSH-triggered restart. Core actions never consume
                    // the advisory identifier, so a missing advisory does not block this.
                    await sink.TriggerCoreUpdateAsync();
                    return "Triggered BTCPay Server update over SSH.";

                case SecSwitchAction.ShutdownCore:
                    sink.StopApplication();
                    return "Stopped BTCPay Server due to an unfixable core advisory.";

                default:
                    // Fails closed on an out-of-range enum value (e.g. an unchecked cast) instead of
                    // falling through to a destructive branch.
                    return $"Unknown action {action}; nothing done.";
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch action {Action} for {Identifier} failed", action, Sanitize(advisory?.Identifier));
            return $"Action {action} failed: {Sanitize(e.Message)}";
        }
    }

    private string Disable(Advisory? advisory)
    {
        if (!IsSafeIdentifier(advisory?.Identifier))
            return $"Refusing to disable: plugin identifier '{Sanitize(advisory?.Identifier)}' is missing or unsafe.";
        if (!sink.QueueDisable(advisory!.Identifier))
            return $"Failed to queue disable of {advisory.Identifier}: no installed plugin matches that identifier.";
        sink.StopApplication();
        return $"Queued disable of {advisory.Identifier}; stopping for restart.";
    }

    // A plugin identifier ultimately feeds a file path inside BtcPayActionSink, and neither primitive
    // it calls validates that for us first: PluginManager.DisablePlugin only appends a text line
    // ("disable:<identifier>") to a queue file, which core replays against its own traversal check
    // (PluginManager.AssertSafeName) on the *next* process start with no surrounding try/catch - a
    // traversal-y identifier queued now would crash the server on the very restart SecSwitch just
    // triggered to apply the fix. PluginService.DownloadRemotePlugin builds a file path from the
    // identifier immediately, before any such check runs. So refuse anything that is not a plain
    // name here, before either call is ever reached.
    private static bool IsSafeIdentifier(string? identifier)
    {
        if (identifier is null || !SafeIdentifierPattern.IsMatch(identifier))
            return false;
        // '.' is a legal character above (real identifiers are dotted), so a bare ".." segment is
        // not excluded by the character allowlist alone and must still be rejected explicitly.
        return !identifier.Contains("..", StringComparison.Ordinal);
    }

    // Outcome strings and log messages sometimes carry a *rejected* identifier, or an exception's own
    // Message, verbatim - both are untrusted at that point (that is precisely why the identifier was
    // rejected), and an SSH exception's Message can carry host/connection detail. Cap the length and
    // drop control characters (plus the two Unicode separators char.IsControl misses - see
    // SafeIdentifierPattern's remarks) before either reaches the admin UI, the ledger, or a log sink.
    // The two rejected characters below are written as \u escapes, not literal characters, on
    // purpose: U+2028/U+2029 are visually indistinguishable from a plain space in most editors and a
    // literal copy of either is easy to silently corrupt into something else entirely.
    private static string Sanitize(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var cleaned = new string(value.Where(c => !char.IsControl(c) && c != '\u2028' && c != '\u2029').ToArray());
        return cleaned.Length > maxLength ? cleaned[..maxLength] + "..." : cleaned;
    }
}

/// <summary>
/// The real <see cref="IActionSink"/>: thin wrappers over BTCPayServer's own plugin-management and
/// process-lifetime primitives. Contains no policy logic - <see cref="ActionExecutor"/> decides what
/// to call and whether an identifier is safe to use; this class only calls it.
///
/// <see cref="QueueDisable"/> and <see cref="QueueUpdateAsync"/> resolve the identifier they are
/// given against the actual on-disk plugin directory, case-insensitively, before calling into core,
/// and use the resolved (exact on-disk) casing for every downstream call. This matters because core
/// itself is inconsistent about case: AdvisoryApplicability matches an advisory to an installed
/// plugin with OrdinalIgnoreCase on purpose (a case mismatch there must not cause a vulnerable
/// plugin to be missed), but PluginManager resolves a *queued* disable/install case-sensitively and
/// existence-gated - ExecuteCommand's "disable"/"install" cases require
/// Directory.Exists(Path.Join(pluginsFolder, identifier)) (PluginManager.cs:453,485), and
/// GetDisabledPluginIdentifiers keys off a default-comparer, i.e. ordinal, HashSet
/// (PluginManager.cs:590-594). Queueing the advisory's own spelling unresolved would let a
/// same-plugin-different-casing advisory pass applicability, get queued, and stop the server - then
/// silently no-op on replay because the directory "doesn't exist" under that casing, leaving the
/// vulnerable plugin loaded while the outcome string falsely claims it was disabled.
/// </summary>
public sealed class BtcPayActionSink(
    PluginService pluginService,
    IOptions<DataDirectories> dataDirectories,
    BTCPayServerOptions serverOptions,
    CheckConfigurationHostedService sshState,
    IHostApplicationLifetime lifetime,
    ILogger<BtcPayActionSink> logger) : IActionSink
{
    public bool QueueDisable(string identifier)
    {
        if (!TryResolveInstalledDirectory(dataDirectories.Value.PluginDir, identifier, out var resolved))
        {
            logger.LogWarning(
                "SecSwitch could not resolve plugin identifier {Identifier} to an installed plugin directory; not queueing a disable.",
                identifier);
            return false;
        }
        PluginManager.DisablePlugin(dataDirectories.Value.PluginDir, resolved);
        return true;
    }

    public async Task<bool> QueueUpdateAsync(string identifier, string version)
    {
        if (!TryResolveInstalledDirectory(dataDirectories.Value.PluginDir, identifier, out var resolved))
        {
            logger.LogWarning(
                "SecSwitch could not resolve plugin identifier {Identifier} to an installed plugin directory; not queueing an update.",
                identifier);
            return false;
        }
        // Two separate calls: downloading a plugin does not queue its install. Both use the
        // resolved on-disk casing so the files DownloadRemotePlugin writes are the same path
        // InstallPlugin's queued command later looks for - core's "install" replay builds that
        // path from whatever string was queued, case-sensitively (PluginManager.cs:453,464-482).
        await pluginService.DownloadRemotePlugin(resolved, version);
        pluginService.InstallPlugin(resolved);
        return true;
    }

    /// <summary>
    /// Finds the on-disk plugin directory matching <paramref name="identifier"/> case-insensitively
    /// and returns its exact on-disk casing. Public and static so it can be tested directly against
    /// a real (temporary) directory, without constructing this class's full BTCPayServer DI graph.
    /// </summary>
    public static bool TryResolveInstalledDirectory(string pluginDir, string identifier, out string resolvedIdentifier)
    {
        resolvedIdentifier = identifier;
        if (string.IsNullOrEmpty(identifier) || !Directory.Exists(pluginDir))
            return false;
        foreach (var dir in Directory.EnumerateDirectories(pluginDir))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, identifier, StringComparison.OrdinalIgnoreCase))
            {
                resolvedIdentifier = name;
                return true;
            }
        }
        return false;
    }

    public async Task TriggerCoreUpdateAsync()
    {
        if (!sshState.CanUseSSH || serverOptions.SSHSettings is null)
            throw new InvalidOperationException("SSH is not configured; cannot trigger a core update.");

        using var client = await serverOptions.SSHSettings.ConnectAsync();
        // Mirrors UIServerController.RunSSH/RunSSHCore: same command string, same RunBash helper.
        // RunSSH itself never awaits RunSSHCore's completion (`_ = RunSSHCore(...)`), precisely
        // because RunBash blocks until the exec channel closes, and nohup+disown here only
        // redirects btcpay-update.sh's stdout/stderr, not its inherited stdin - the classic case
        // where a backgrounded job keeps an SSH exec channel open server-side for as long as that
        // job runs (here, however many minutes it takes to bring the whole stack down and back up),
        // independent of anything our side of the connection does. We cannot fire-and-forget in
        // quite the same way - we still want to catch a fast failure (bad auth, unreachable host,
        // command not found) and report it - so we await with a short CommandTimeout instead of
        // RunSSHCore's 60 seconds: long enough to observe a fast local/connection failure, short
        // enough not to block a policy sweep on a command that may legitimately keep running for
        // minutes. Hitting that timeout throws SshOperationTimeoutException (confirmed against the
        // SSH.NET 2025.1.0 source used here: CommandTimeout cancels our own local wait via
        // CancelAsync and sets that exception once the wait is cancelled) - it means our local wait
        // was cancelled, not that the remote command failed, so it is treated as success: dispatched,
        // still running remotely.
        const string command = ". /etc/profile.d/btcpay-env.sh && nohup btcpay-update.sh > /dev/null 2>&1 & disown";
        try
        {
            var result = await client.RunBash(command, TimeSpan.FromSeconds(10));
            logger.LogInformation("SecSwitch triggered btcpay-update.sh over SSH; exit status {ExitStatus}", result.ExitStatus);
        }
        catch (SshOperationTimeoutException)
        {
            logger.LogInformation(
                "SecSwitch dispatched btcpay-update.sh over SSH; the local wait timed out as expected while it keeps running remotely.");
        }
    }

    public void StopApplication() => lifetime.StopApplication();
}
