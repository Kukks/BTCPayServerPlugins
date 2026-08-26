using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.SSH;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// The destructive primitives a <see cref="PolicyDecision"/> can trigger. Exists so
/// <see cref="ActionExecutor"/> can be tested without disabling a real plugin or stopping the
/// process - <see cref="BtcPayActionSink"/> is the only implementation that touches BTCPayServer.
/// <see cref="QueueDisable"/> and <see cref="QueueUpdateAsync"/> report whether anything was
/// actually queued: the identifier they are given may not resolve to anything the sink can act on,
/// or may resolve ambiguously (see <see cref="BtcPayActionSink"/>'s remarks), and a caller must not
/// stop the application on the strength of a queue that never happened.
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
    //
    // Anchored with \A/\z, not ^/$: in .NET regex (without RegexOptions.Multiline), $ matches either
    // at the end of the string OR immediately before a single trailing '\n' - confirmed empirically
    // ("Plug\n" matches "^...{1,128}$"). \A and \z both mean "absolute start/end of the string, no
    // exceptions", closing that gap.
    private static readonly Regex SafeIdentifierPattern = new(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.Compiled);

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
                        return $"Failed to queue update of {Sanitize(advisory.Identifier)}: no installed plugin matches that identifier unambiguously.";
                    sink.StopApplication();
                    return $"Queued update of {Sanitize(advisory.Identifier)} to {Sanitize(advisory.FixedVersion)}; stopping for restart.";

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
            return $"Failed to queue disable of {Sanitize(advisory.Identifier)}: no installed plugin matches that identifier unambiguously.";
        sink.StopApplication();
        return $"Queued disable of {Sanitize(advisory.Identifier)}; stopping for restart.";
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

    // Outcome strings and log messages sometimes carry an identifier or an exception's own Message
    // verbatim. Applied even to identifiers that already passed IsSafeIdentifier - an upstream guard
    // is not a substitute for sanitizing what we actually emit, and a *rejected* identifier is by
    // definition not clean to begin with. An SSH exception's Message can also carry host/connection
    // detail. Cap the length and drop control characters (plus the two Unicode separators
    // char.IsControl misses - see SafeIdentifierPattern's remarks) before either reaches the admin
    // UI, the ledger, or a log sink.
    // The two rejected characters below are written as \u escapes, not literal characters, on
    // purpose: U+2028/U+2029 are visually indistinguishable from a plain space in most editors and a
    // literal copy of either is easy to silently corrupt into something else entirely.
    // internal (not private): SecSwitchNotification.Handler.FillViewModel needs the exact same
    // truncate-and-strip-control-characters treatment for advisory-derived text reaching the admin
    // notification list, and duplicating it risks the two copies drifting apart. Still not public -
    // this stays an implementation detail shared within the assembly, not part of the plugin's API.
    internal static string Sanitize(string? value, int maxLength = 200)
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
///
/// Resolution prefers an exact (ordinal) match, and refuses to guess between two or more
/// differently-cased directories that all match case-insensitively but not exactly: on a
/// case-sensitive filesystem (production Linux) two such directories can coexist - the update path
/// itself can create one, since PluginManager's "install" replay (PluginManager.cs:464-482) has no
/// Directory.Exists gate, only a File.Exists check on the downloaded .btcpay file, so extracting an
/// update queued under the wrong casing creates a same-plugin sibling rather than overwriting the
/// original. Directory.EnumerateDirectories's enumeration order is not guaranteed, so picking
/// arbitrarily between two such siblings could disable/update the wrong one while leaving the real,
/// vulnerable plugin directory untouched.
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
        if (!TryResolveInstalledDirectory(dataDirectories.Value.PluginDir, identifier, out var resolved, out var ambiguous))
        {
            LogUnresolved("disable", identifier, ambiguous);
            return false;
        }
        PluginManager.DisablePlugin(dataDirectories.Value.PluginDir, resolved);
        return true;
    }

    public async Task<bool> QueueUpdateAsync(string identifier, string version)
    {
        if (!TryResolveInstalledDirectory(dataDirectories.Value.PluginDir, identifier, out var resolved, out var ambiguous))
        {
            LogUnresolved("update", identifier, ambiguous);
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

    private void LogUnresolved(string actionVerb, string identifier, IReadOnlyList<string> ambiguous)
    {
        if (ambiguous.Count > 0)
            logger.LogWarning(
                "SecSwitch found multiple installed plugin directories matching {Identifier} case-insensitively ({Candidates}); refusing to guess which one to {ActionVerb}.",
                identifier, string.Join(", ", ambiguous), actionVerb);
        else
            logger.LogWarning(
                "SecSwitch could not resolve plugin identifier {Identifier} to an installed plugin directory; the {ActionVerb} was not queued.",
                identifier, actionVerb);
    }

    /// <summary>
    /// Finds the on-disk plugin directory matching <paramref name="identifier"/>. An exact (ordinal)
    /// match always wins outright. Failing that, exactly one case-insensitive match resolves to that
    /// match's exact on-disk casing; zero or two-or-more case-insensitive matches both fail to
    /// resolve (the latter via <paramref name="ambiguousMatches"/>, populated only in the
    /// two-or-more case, so a caller can log which candidates it refused to choose between). Public
    /// and static so it can be tested directly against a real (temporary) directory, without
    /// constructing this class's full BTCPayServer DI graph.
    /// </summary>
    public static bool TryResolveInstalledDirectory(
        string pluginDir, string identifier, out string resolvedIdentifier, out IReadOnlyList<string> ambiguousMatches)
    {
        resolvedIdentifier = identifier;
        ambiguousMatches = [];
        if (string.IsNullOrEmpty(identifier) || !Directory.Exists(pluginDir))
            return false;

        var names = Directory.EnumerateDirectories(pluginDir).Select(dir => Path.GetFileName(dir)!);
        return TryResolveAmongCandidates(names, identifier, out resolvedIdentifier, out ambiguousMatches);
    }

    /// <summary>
    /// The matching rule itself, kept separate from <see cref="TryResolveInstalledDirectory"/>'s
    /// filesystem call so it can be unit-tested with an explicit, platform-independent list of
    /// candidate names: two directories differing only by case cannot be reliably created side by
    /// side on a real temporary directory on this codebase's own Windows dev/CI environment (NTFS is
    /// case-insensitive by default there), even though that is exactly the scenario that matters on
    /// the case-sensitive Linux filesystems SecSwitch actually runs against in production.
    /// </summary>
    public static bool TryResolveAmongCandidates(
        IEnumerable<string> candidateNames, string identifier, out string resolvedIdentifier, out IReadOnlyList<string> ambiguousMatches)
    {
        resolvedIdentifier = identifier;
        ambiguousMatches = [];

        var caseInsensitiveMatches = new List<string>();
        foreach (var name in candidateNames)
        {
            if (string.Equals(name, identifier, StringComparison.Ordinal))
            {
                // An exact match is unambiguous and wins outright, regardless of any
                // case-insensitive collision found so far or still to come.
                resolvedIdentifier = name;
                return true;
            }
            if (string.Equals(name, identifier, StringComparison.OrdinalIgnoreCase))
                caseInsensitiveMatches.Add(name);
        }

        if (caseInsensitiveMatches.Count == 1)
        {
            resolvedIdentifier = caseInsensitiveMatches[0];
            return true;
        }

        // Either nothing matched (list stays empty), or two-or-more differently-cased directories
        // claim the same identifier and enumeration order is not a safe way to pick between them.
        ambiguousMatches = caseInsensitiveMatches;
        return false;
    }

    public async Task TriggerCoreUpdateAsync()
    {
        if (!sshState.CanUseSSH || serverOptions.SSHSettings is null)
            throw new InvalidOperationException("SSH is not configured; cannot trigger a core update.");

        var client = await serverOptions.SSHSettings.ConnectAsync();
        // Mirrors UIServerController.RunSSH/RunSSHCore's command string and its use of the RunBash
        // helper, but NOT its CommandTimeout: RunSSHCore passes TimeSpan.FromMinutes(1.0), which
        // (confirmed against the SSH.NET 2025.1.0 source used here, SshCommand.cs:296-301) arms a
        // CancellationTokenSource off CommandTimeout; hitting that timeout calls CancelAsync
        // (SshCommand.cs:439-475), which sends an actual SSH "signal" channel request ("TERM") to
        // the remote process - not a purely local give-up. btcpay-update.sh backgrounds itself with
        // nohup+disown, which protects it from SIGHUP, not SIGTERM, and a non-interactive
        // `bash -c '... &'` does not put the backgrounded job in its own process group, so a
        // CommandTimeout here risks SIGTERMing the very update it just launched. Core carries this
        // same latent risk on its own 60-second value and evidently rarely if ever hits it in
        // practice (its Update button works) - we remove the risk outright rather than lean on that:
        // by never setting CommandTimeout at all (RunBash's `timeout` argument stays unset, so
        // SshCommand.CommandTimeout keeps its Timeout.InfiniteTimeSpan default), nothing ever calls
        // CancelAsync on our behalf, so we never send that signal ourselves.
        //
        // The tradeoff: we can no longer wait for the command to finish without risking exactly the
        // multi-minute block this whole chain of reasoning exists to avoid (a policy sweep should
        // not hang for as long as bringing the whole stack down and up takes), and we cannot safely
        // dispose `client` until the command is done with it. So this method reports "launched", not
        // "completed" - it does not, and cannot, confirm btcpay-update.sh succeeded - and disposal
        // moves to a continuation on the command's own task instead of a `using` here, so it fires
        // once, after the exec task has actually finished with the connection rather than
        // immediately when this method returns.
        const string command = ". /etc/profile.d/btcpay-env.sh && nohup btcpay-update.sh > /dev/null 2>&1 & disown";
        Task<SSHCommandResult> execTask;
        try
        {
            execTask = client.RunBash(command);
        }
        catch
        {
            // RunBash is not itself async - it can throw synchronously, before any Task exists to
            // attach the disposal continuation below to. The reachable case is
            // SshClient.CreateCommand -> EnsureSessionIsOpen throwing SshConnectionException if the
            // session is not open. The exception itself is still contained (it propagates to
            // ActionExecutor's catch like any other failure); without this, only client's socket,
            // Session, and listener thread would leak.
            client.Dispose();
            throw;
        }
        _ = execTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger.LogWarning(t.Exception, "SecSwitch's SSH command for btcpay-update.sh reported an error after being dispatched");
            else if (t.IsCompletedSuccessfully)
                logger.LogInformation("SecSwitch's SSH command for btcpay-update.sh completed with exit status {ExitStatus}", t.Result.ExitStatus);
            client.Dispose();
        }, TaskScheduler.Default);
        logger.LogInformation("SecSwitch dispatched btcpay-update.sh over SSH; not waiting for it to finish.");
    }

    public void StopApplication() => lifetime.StopApplication();
}
