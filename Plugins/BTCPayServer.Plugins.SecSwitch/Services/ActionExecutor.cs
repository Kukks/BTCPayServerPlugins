using System;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// The destructive primitives a <see cref="PolicyDecision"/> can trigger. Exists so
/// <see cref="ActionExecutor"/> can be tested without disabling a real plugin or stopping the
/// process - <see cref="BtcPayActionSink"/> is the only implementation that touches BTCPayServer.
/// </summary>
public interface IActionSink
{
    void QueueDisable(string identifier);
    Task QueueUpdateAsync(string identifier, string version);
    Task TriggerCoreUpdateAsync();
    void StopApplication();
}

/// <summary>
/// Carries out a policy decision against a live instance. This is the only place in SecSwitch that
/// contains the decision-to-primitive mapping; it never calls a BTCPayServer API directly (that all
/// lives behind <see cref="IActionSink"/>) and it never lets an exception - or a hostile/malformed
/// input - escape as a thrown exception. Every path returns a human-readable outcome instead.
/// </summary>
public sealed class ActionExecutor(IActionSink sink, ILogger<ActionExecutor> logger)
{
    public async Task<string> ExecuteAsync(SecSwitchAction action, Advisory advisory)
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
                        return $"Refusing to update: plugin identifier '{advisory.Identifier}' is missing or unsafe.";
                    await sink.QueueUpdateAsync(advisory.Identifier, advisory.FixedVersion);
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
            logger.LogError(e, "SecSwitch action {Action} for {Identifier} failed", action, advisory?.Identifier);
            return $"Action {action} failed: {e.Message}";
        }
    }

    private string Disable(Advisory? advisory)
    {
        if (!IsSafeIdentifier(advisory?.Identifier))
            return $"Refusing to disable: plugin identifier '{advisory?.Identifier}' is missing or unsafe.";
        sink.QueueDisable(advisory!.Identifier);
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
        if (string.IsNullOrWhiteSpace(identifier))
            return false;
        if (identifier.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in identifier)
        {
            if (c is '/' or '\\' or ':' || char.IsControl(c))
                return false;
        }
        return true;
    }
}

/// <summary>
/// The real <see cref="IActionSink"/>: thin wrappers over BTCPayServer's own plugin-management and
/// process-lifetime primitives. Contains no policy logic - <see cref="ActionExecutor"/> decides what
/// to call and whether an identifier is safe to use; this class only calls it.
/// </summary>
public sealed class BtcPayActionSink(
    PluginService pluginService,
    IOptions<DataDirectories> dataDirectories,
    BTCPayServerOptions serverOptions,
    CheckConfigurationHostedService sshState,
    IHostApplicationLifetime lifetime,
    ILogger<BtcPayActionSink> logger) : IActionSink
{
    public void QueueDisable(string identifier)
        => PluginManager.DisablePlugin(dataDirectories.Value.PluginDir, identifier);

    public async Task QueueUpdateAsync(string identifier, string version)
    {
        // Two separate calls: downloading a plugin does not queue its install.
        await pluginService.DownloadRemotePlugin(identifier, version);
        pluginService.InstallPlugin(identifier);
    }

    public async Task TriggerCoreUpdateAsync()
    {
        if (!sshState.CanUseSSH || serverOptions.SSHSettings is null)
            throw new InvalidOperationException("SSH is not configured; cannot trigger a core update.");

        using var client = await serverOptions.SSHSettings.ConnectAsync();
        // Mirrors UIServerController.RunSSH/RunSSHCore exactly: RunBash is core's own SSH-execute
        // helper - it wraps the command in `bash -c`, applies the timeout, and disposes the
        // underlying SshCommand itself. nohup+disown detaches btcpay-update.sh from this SSH
        // session so the update keeps running once the session closes; btcpay-update.sh brings the
        // whole stack down and back up on its own, which is why ActionExecutor does not also call
        // StopApplication for this action.
        const string command = ". /etc/profile.d/btcpay-env.sh && nohup btcpay-update.sh > /dev/null 2>&1 & disown";
        var result = await client.RunBash(command, TimeSpan.FromMinutes(1.0));
        logger.LogInformation("SecSwitch triggered btcpay-update.sh over SSH; exit status {ExitStatus}", result.ExitStatus);
    }

    public void StopApplication() => lifetime.StopApplication();
}
