using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
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
    ILogger<SecSwitchPeriodicTask> logger) : IPeriodicTask
{
    /// <summary>
    /// Named (not typed) HttpClient registration - see the class doc comment for why. Registered in
    /// <see cref="SecSwitchPlugin.Execute"/> via <c>services.AddHttpClient(HttpClientName)</c>.
    /// </summary>
    public const string HttpClientName = "SecSwitch";

    // Best-effort, in-process "already handled this run" guards so a healthy poll does not re-check
    // the ledger every single tick once it knows the answer - NOT the source of truth for the trust
    // bootstrap (SecSwitchLedger.TrustRootBootstrapped is, checked fresh below the first time), and
    // deliberately left false on failure so a LATER tick in the SAME process retries rather than the
    // failure going permanently unnoticed for the rest of this process's life.
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
    /// logic. Runs unconditionally, independent of <see cref="SecSwitchSettings.Enabled"/>: the
    /// settings page refuses to let an admin enable SecSwitch with zero trusted keys at all, so
    /// bootstrapping only while already enabled would be a deadlock. Gated on
    /// <see cref="SecSwitchLedger.TrustRootBootstrapped"/>, which - once set - is never re-examined by
    /// this method again for the life of the ledger, so a bundled key an admin later removes on
    /// purpose never silently reappears on a subsequent restart.
    /// </summary>
    async Task BootstrapTrustStoreOnceAsync()
    {
        if (_trustRootChecked)
            return;
        try
        {
            var ledgerState = await ledger.GetAsync();
            if (ledgerState.TrustRootBootstrapped)
            {
                _trustRootChecked = true;
                return;
            }

            var resourceBytes = TrustRootBootstrapper.ReadEmbeddedTrustRoot(logger);
            if (resourceBytes is null)
                return; // Fails closed with a log already emitted inside ReadEmbeddedTrustRoot.
                        // _trustRootChecked deliberately left false: this build's resource is broken
                        // or absent on EVERY tick, but a future plugin upgrade fixing it should still
                        // get a chance to bootstrap rather than being stuck on a stale, unset flag.

            var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>() ?? new SecSwitchSettings();
            settings.TrustedKeys = TrustRootBootstrapper.Apply(settings.TrustedKeys, resourceBytes, logger);
            await settingsRepository.UpdateSetting(settings);
            await ledger.RecordTrustRootBootstrapAsync();
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

            // Hard requirement 1 (Task 13): AdvisoryFetcher.FetchAsync's second parameter is compared
            // against an AdvisoryIndexEntry's CONTENT HASH, not an advisory id - passing ids here
            // would never match anything, so every advisory would be re-downloaded on every single
            // poll, forever. Built from the ledger's persisted LedgerEntry.ContentHash values,
            // skipping any withheld/empty one (see SecSwitchMonitor.RecordAsync's own doc comment for
            // when a hash is withheld rather than cached). SecSwitchMonitor.ProcessAsync's own
            // IsActedAsync check remains the authoritative dedupe - this is only an optimisation that
            // avoids a network round trip for something already fully resolved.
            var ledgerState = await ledger.GetAsync();
            var knownContentHashes = ledgerState.Entries.Values
                .Select(e => e.ContentHash)
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fetcher = new AdvisoryFetcher(httpClientFactory.CreateClient(HttpClientName));
            var fetched = await fetcher.FetchAsync(settings.FeedUrl, knownContentHashes, cancellationToken);
            if (fetched.Count == 0)
                return;

            var state = BuildState(installedPlugins, environment.Version, sshState.CanUseSSH);
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
    /// class's full DI graph.
    /// </summary>
    public static InstanceState BuildState(
        IEnumerable<IBTCPayServerPlugin> plugins, string coreVersion, bool canUseSsh)
    {
        var installed = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins ?? [])
        {
            if (plugin is null || string.IsNullOrEmpty(plugin.Identifier))
                continue;
            installed[plugin.Identifier] = plugin.Version;
        }

        // Mirrors PluginService.GetShortBtcpayVersion (BTCPayServer/Plugins/PluginManager/PluginService.cs):
        // strip the leading 'v' and any +buildmeta - BTCPayServerEnvironment.Version carries both, and
        // System.Version.TryParse cannot handle either.
        var shortVersion = (coreVersion ?? "").TrimStart('v').Split('+')[0];
        var parsed = Version.TryParse(shortVersion, out var v) ? v : new Version(0, 0);

        return new InstanceState(installed, parsed, canUseSsh);
    }
}
