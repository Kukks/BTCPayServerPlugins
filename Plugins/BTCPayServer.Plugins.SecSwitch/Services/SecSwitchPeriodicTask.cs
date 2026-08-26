using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Notifications;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// The wiring that makes SecSwitch actually run: an hourly poll (registered via
/// <c>AddScheduledTask&lt;SecSwitchPeriodicTask&gt;</c> in <see cref="SecSwitchPlugin"/>) that records
/// a startup heartbeat, bootstraps the trust store from the embedded resource, then fetches and
/// processes new advisories. Every other Task 2-12 component already exists and is independently
/// hardened to fail closed; nothing calls any of them until this class exists.
///
/// <see cref="Do"/> must never throw - an unhandled exception here is exactly how this silently stops
/// protecting the instance (core's own <c>PeriodicTaskLauncherHostedService</c> catches and logs a
/// throwing task, but still must not be relied on as the only backstop - see the per-phase comments
/// below). Each of the three phases (heartbeat, trust bootstrap, advisory poll) is independently
/// wrapped so a failure in one can never prevent the other two from running on the same tick, mirroring
/// the per-item-plus-structural-backstop posture <see cref="SecSwitchMonitor.ProcessAsync"/> and
/// <see cref="AdvisoryFetcher.FetchAsync(string,System.Collections.Generic.ISet{string},System.Threading.CancellationToken)"/>
/// already use - a final outer try/catch is a backstop for anything above those three, not the only
/// line of defence.
///
/// <see cref="AdvisoryFetcher"/> is deliberately NOT constructor-injected. <c>AddScheduledTask&lt;T&gt;</c>
/// registers this class as a singleton (core, <c>Extensions.cs</c>:
/// <c>services.TryAddSingleton&lt;T&gt;()</c>), so a typed <see cref="HttpClient"/> captured at
/// construction would never rotate its handler for the life of the process, going DNS-stale on a
/// long-running server exactly the way <see cref="Services.ActionExecutor"/>'s own doc comment warns
/// a captive dependency would (see <c>BtcPayActionSink</c>'s remarks on <c>PluginService</c>, the same
/// failure shape). <see cref="httpClientFactory"/> instead hands out a client from a NAMED
/// registration (<see cref="HttpClientName"/>) fresh on every poll, and a new
/// <see cref="AdvisoryFetcher"/> is constructed around it each time - <c>AdvisoryFetcher(HttpClient)</c>'s
/// constructor itself is unchanged, so this is purely a wiring choice.
/// </summary>
public sealed class SecSwitchPeriodicTask(
    ISettingsRepository settingsRepository,
    IHttpClientFactory httpClientFactory,
    SecSwitchMonitor monitor,
    LedgerStore ledger,
    NotificationSender notificationSender,
    IEnumerable<IBTCPayServerPlugin> installedPlugins,
    BTCPayServerEnvironment environment,
    CheckConfigurationHostedService sshState,
    BTCPayServerOptions serverOptions,
    ILogger<SecSwitchPeriodicTask> logger) : IPeriodicTask
{
    /// <summary>
    /// Named (not typed) HttpClient registration - see the class doc comment for why. Registered in
    /// <see cref="SecSwitchPlugin.Execute"/> via <c>services.AddHttpClient(HttpClientName)</c>.
    /// </summary>
    public const string HttpClientName = "SecSwitch";

    // Best-effort, in-process "already handled this run" guards so a healthy poll does not re-check
    // the ledger every single tick once it knows the answer - NOT the source of truth for the trust
    // bootstrap (SecSwitchLedger.OfferedTrustRootFingerprints is, checked fresh below every time this
    // is false), and deliberately left false on failure so a LATER tick in the SAME process retries
    // rather than the failure going permanently unnoticed for the rest of this process's life.
    bool _heartbeatRecorded;
    bool _trustRootChecked;

    public async Task Do(CancellationToken cancellationToken)
    {
        try
        {
            await RecordHeartbeatOnceAsync();
            await BootstrapTrustStoreOnceAsync();
            await PollForAdvisoriesAsync(cancellationToken);
        }
        catch (Exception e)
        {
            // Structural backstop only - every phase above already has its own guard. Exists so a
            // future change that misses one still fails toward "try again next poll" instead of
            // taking the whole scheduled task down. See the class doc comment.
            logger.LogError(e, "SecSwitch periodic task failed unexpectedly");
        }
    }

    /// <summary>
    /// Hard requirement 4 (Task 13): SecSwitch can be silently disabled by an unrelated plugin's crash
    /// - BTCPayServer forces <c>SystemPlugin = false</c> on every externally-loaded plugin, and one
    /// startup failure mode it cannot cleanly attribute to a single component falls back to disabling
    /// every externally-installed plugin at once (see <c>PluginManager.cs</c>/<c>Program.cs</c>), this
    /// plugin included, regardless of whether SecSwitch itself did anything wrong. A recorded startup
    /// time is what makes that silence detectable at all. Recorded once per process start - retried on
    /// a later tick within the SAME process if the write itself fails, since
    /// <see cref="LedgerStore.RecordStartupAsync"/> is a plain read-modify-write with no internal
    /// fail-closed guard of its own (unlike most of this plugin's other components).
    /// </summary>
    async Task RecordHeartbeatOnceAsync()
    {
        if (_heartbeatRecorded)
            return;
        try
        {
            await ledger.RecordStartupAsync(DateTimeOffset.UtcNow);
            _heartbeatRecorded = true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch failed to record its startup heartbeat");
        }
    }

    /// <summary>
    /// Hard requirement 3 (Task 13): bootstraps <see cref="SecSwitchSettings.TrustedKeys"/> from the
    /// embedded trust-root resource - see <see cref="TrustRootBootstrapper"/> for the actual admission
    /// logic and <see cref="SecSwitchLedger.OfferedTrustRootFingerprints"/>'s own doc comment for why
    /// "already ran" is tracked per-fingerprint rather than as a single latch (Task 13 review, Finding
    /// I1 fix). Runs unconditionally, independent of <see cref="SecSwitchSettings.Enabled"/>: the
    /// settings page refuses to let an admin enable SecSwitch with zero trusted keys at all, so
    /// bootstrapping only while already enabled would be a deadlock.
    ///
    /// The settings write is skipped entirely when nothing new was offered this run (today's shipped
    /// resource - an empty <c>keys</c> array - is always this case): both because there is nothing to
    /// persist, and because it removes the one theoretical lost-update race this method could
    /// otherwise cause against SecSwitchController's own unguarded SecSwitchSettings read-modify-write
    /// endpoints for the only scenario that exists today.
    /// </summary>
    async Task BootstrapTrustStoreOnceAsync()
    {
        if (_trustRootChecked)
            return;
        try
        {
            var resourceBytes = TrustRootBootstrapper.ReadEmbeddedTrustRoot(logger);
            if (resourceBytes is null)
                return; // Fails closed with a log already emitted inside ReadEmbeddedTrustRoot.
                        // _trustRootChecked deliberately left false: this build's resource is broken
                        // or absent on EVERY tick, but a future plugin upgrade fixing it should still
                        // get a chance to bootstrap rather than being stuck on a stale, unset flag.

            var ledgerState = await ledger.GetAsync();
            var alreadyOffered = new HashSet<string>(
                ledgerState.OfferedTrustRootFingerprints ?? [], StringComparer.OrdinalIgnoreCase);

            var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
            var (updatedKeys, newlyOffered) =
                TrustRootBootstrapper.Apply(settings.TrustedKeys, resourceBytes, alreadyOffered, logger);

            if (newlyOffered.Count == 0)
            {
                // Nothing new to offer - every fingerprint the bundle currently lists (if any) was
                // already considered by a prior run. Skip both writes; see the method doc comment.
                _trustRootChecked = true;
                return;
            }

            settings.TrustedKeys = updatedKeys;
            await settingsRepository.UpdateSetting(settings);
            await ledger.RecordTrustRootOfferedAsync(newlyOffered);
            _trustRootChecked = true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch failed to bootstrap its trust store from the embedded resource");
        }
    }

    /// <summary>
    /// The actual poll: fetch new advisories, process them, notify on anything recorded that an admin
    /// should see. Never fetches over the network while <see cref="SecSwitchSettings.Enabled"/> is not
    /// true (default false - opt-in); <see cref="SecSwitchMonitor.ProcessAsync"/> also short-circuits
    /// on a disabled/null settings, but that alone does not stop the network round trip this method
    /// makes BEFORE ever calling it.
    /// </summary>
    async Task PollForAdvisoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>();
            if (settings is not { Enabled: true })
                return;

            var ledgerState = await ledger.GetAsync();
            var knownContentHashes = BuildKnownContentHashes(ledgerState);

            var fetcher = new AdvisoryFetcher(httpClientFactory.CreateClient(HttpClientName));
            var fetched = await fetcher.FetchAsync(settings.FeedUrl, knownContentHashes, cancellationToken);
            if (fetched.Count == 0)
                return;

            // Task 13 review, Finding I2 (Important): CheckConfigurationHostedService.StartAsync fires
            // its SSH connectivity probe WITHOUT awaiting it, and CanUseSSH only becomes true after
            // that probe succeeds - on failure it retries with backoff out to 10 minutes, staying false
            // throughout. Because the scheduled-task launcher enqueues every task immediately on
            // startup (see BuildState's own doc comment), this poll's very first run can race that
            // still-in-flight probe. sshConfigured distinguishes "no SSH configured at all"
            // (serverOptions.SSHSettings is null - exactly what CheckConfigurationHostedService itself
            // gates its probe on, and what UIServerController.cs:855 checks alongside CanUseSSH) from
            // "configured, but not yet verified" (SSHSettings is not null yet CanUseSSH is still
            // false) - PolicyResolver.Resolve treats the latter as a reason to DEFER a fixable core
            // advisory to a later poll rather than resolving it as ShutdownCore, which
            // SecSwitchMonitor would otherwise record as Acted - a TERMINAL ledger status that could
            // never later self-correct to the real UpdateCore once the probe actually finishes.
            var sshConfigured = serverOptions.SSHSettings is not null;
            var state = BuildState(
                installedPlugins, environment.Version, sshState.CanUseSSH,
                sshVerificationPending: sshConfigured && !sshState.CanUseSSH);
            var recorded = await monitor.ProcessAsync(fetched, state, settings, cancellationToken);

            await NotifyAsync(recorded);

            logger.LogInformation("SecSwitch processed {Count} new advisory record(s) this poll", recorded.Count);
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch advisory poll failed unexpectedly");
        }
    }

    /// <summary>
    /// Hard requirement 1 (Task 13), extracted into its own directly-testable method (Task 13 review,
    /// Finding I4 - the brief's own test plan never exercised this line, the one whose regression
    /// re-downloads every advisory on every poll forever): builds the set
    /// <see cref="AdvisoryFetcher.FetchAsync(string,System.Collections.Generic.ISet{string},System.Threading.CancellationToken)"/>
    /// compares against an <c>AdvisoryIndexEntry</c>'s CONTENT HASH - not an advisory id, which would
    /// never match anything - from the ledger's persisted <see cref="LedgerEntry.ContentHash"/> values,
    /// skipping any withheld/empty one (see <c>SecSwitchMonitor.RecordAsync</c>'s own doc comment for
    /// when a hash is withheld rather than cached). <see cref="SecSwitchMonitor.ProcessAsync"/>'s own
    /// <c>IsActedAsync</c> check remains the authoritative dedupe - this is only an optimisation that
    /// avoids a network round trip for something already fully resolved.
    /// </summary>
    internal static ISet<string> BuildKnownContentHashes(SecSwitchLedger ledgerState)
    {
        if (ledgerState?.Entries is null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return ledgerState.Entries.Values
            .Select(e => e.ContentHash)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Notifies the admin bell icon for anything this poll recorded that either was acted on
    /// automatically or is waiting on a manual decision - anything else (Rejected, Unverified,
    /// NeedsAttention, NotApplicable) is not something an admin needs to be interrupted for, and
    /// remains visible in the audit log regardless. One notification failing to send must not stop the
    /// others in the same batch, matching the per-item posture used throughout this plugin.
    /// </summary>
    async Task NotifyAsync(IReadOnlyList<LedgerEntry> recorded)
    {
        foreach (var entry in recorded)
        {
            if (entry.Status is not (LedgerStatus.Acted or LedgerStatus.NeedsDecision))
                continue;

            try
            {
                await notificationSender.SendNotification(new AdminScope(), new SecSwitchNotification
                {
                    AdvisoryId = entry.AdvisoryId,
                    Title = entry.Title,
                    Severity = entry.Severity,
                    Outcome = entry.Reason,
                    NeedsDecision = entry.Status == LedgerStatus.NeedsDecision
                });
            }
            catch (Exception e)
            {
                logger.LogError(e, "SecSwitch failed to send an admin notification for advisory {Id}", entry.AdvisoryId);
            }
        }
    }

    /// <summary>
    /// Maps installed plugins and the running core version into the <see cref="InstanceState"/>
    /// <see cref="PolicyResolver.Resolve"/> and <see cref="Services.AdvisoryApplicability.IsApplicable"/>
    /// need. Static and taking plain parameters (not <see cref="PluginService"/> or
    /// <see cref="BTCPayServerEnvironment"/> directly) so it is testable without constructing this
    /// class's full DI graph. Reachable at process startup, not an hour later: core's
    /// <c>PeriodicTaskLauncherHostedService.StartAsync</c> enqueues every registered
    /// <c>ScheduledTask</c> immediately and only schedules the NEXT run after the current one
    /// completes (confirmed by reading <c>HostedServices/PeriodicTaskLauncherHostedService.cs</c>), so
    /// <see cref="Do"/>'s first tick - and therefore this method's first call - happens as soon as the
    /// host starts, concurrently with other startup-time probes such as
    /// <see cref="CheckConfigurationHostedService"/>'s own (see <paramref name="sshVerificationPending"/>).
    /// </summary>
    /// <param name="sshVerificationPending">
    /// True only when SSH IS configured but <see cref="CheckConfigurationHostedService.CanUseSSH"/> has
    /// not (yet, or ever) reported success - see the Task 13 review Finding I2 comment in
    /// <see cref="PollForAdvisoriesAsync"/> for the full reasoning. Defaults to false so every call
    /// site that predates this parameter (including this plugin's own existing tests) keeps its
    /// original meaning: "no SSH available, and nothing pending either".
    /// </param>
    public static InstanceState BuildState(
        IEnumerable<IBTCPayServerPlugin> plugins, string coreVersion, bool canUseSsh,
        bool sshVerificationPending = false)
    {
        var installed = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins ?? [])
        {
            if (plugin is null || string.IsNullOrEmpty(plugin.Identifier))
                continue;
            installed[plugin.Identifier] = plugin.Version;
        }

        // Defensively mirrors PluginService.GetShortBtcpayVersion
        // (BTCPayServer/Plugins/PluginManager/PluginService.cs:61 - Env.Version.TrimStart('v').Split('+')[0]):
        // strips a leading 'v' and any +buildmeta suffix IF present, neither of which
        // System.Version.TryParse can handle. This build's own BTCPayServerEnvironment.Version is
        // "2.4.2" - no 'v', no '+' (Build/Version.csproj sets a bare <Version>2.4.2</Version> with no
        // SourceLink/+sha suffix) - so this stripping is a defensive no-op here today, not a
        // description of what this specific string looks like; it exists to match core's own
        // defensive handling for any build configuration where it would not be.
        var shortVersion = (coreVersion ?? "").TrimStart('v').Split('+')[0];
        var parsed = Version.TryParse(shortVersion, out var v) ? v : new Version(0, 0);

        return new InstanceState(installed, parsed, canUseSsh, sshVerificationPending);
    }
}
