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

            // Final whole-branch review, Finding I2 (Important): SecSwitchController now validates
            // FeedUrl on save, but a settings row written before that validation existed - or by any
            // other route - can still hold an unusable value, and AdvisoryFetcher's own refusal is
            // silent by design (it fails closed to "nothing fetched", never throws, and has no
            // logger). Without this, an enabled SecSwitch pointed at an http:// or unparseable feed
            // produces no advisories, no error, and no log line, forever - the exact "looks healthy,
            // protects nothing" state this plugin must never be in. Checked against
            // AdvisoryFetcher's own predicate so the two can never disagree.
            if (!AdvisoryFetcher.IsSupportedFeedUrl(settings.FeedUrl))
            {
                logger.LogError(
                    "SecSwitch is ENABLED but its advisory feed URL is not usable ({FeedUrl}): it must be an " +
                    "absolute https:// URL. No advisory can be fetched, and no advisory can be acted on, until " +
                    "this is corrected on the SecSwitch settings page.",
                    ActionExecutor.Sanitize(settings.FeedUrl));
                return;
            }

            var ledgerState = await ledger.GetAsync();
            var knownContentHashes = BuildKnownContentHashes(ledgerState);
            // Snapshotted BEFORE ProcessAsync overwrites these rows - see NotifyAsync (Finding I4).
            var priorStates = BuildPriorNotificationStates(ledgerState);

            var fetcher = new AdvisoryFetcher(httpClientFactory.CreateClient(HttpClientName));
            var fetchResult = await fetcher.FetchAsync(
                settings.FeedUrl, knownContentHashes, ledgerState.FeedIndexCursor, cancellationToken);

            // Finding C3: persisted BEFORE the "nothing fetched" early return below, not after. A
            // poll that downloads nothing can still have consumed its whole request budget walking a
            // wall of failing entries - that is exactly the case the cursor exists for, and dropping
            // it here would leave the next poll re-walking the identical prefix, which is the bug.
            await ledger.RecordFeedIndexCursorAsync(fetchResult.NextCursor);

            var fetched = fetchResult.Advisories;
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

            await NotifyAsync(recorded, priorStates);

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
    /// automatically, is waiting on a manual decision, or (Task 13 review round 2, Finding R1 fix)
    /// SecSwitch genuinely could not resolve on its own - Rejected, Unverified, and NotApplicable are
    /// the only statuses excluded now: the first two are transient parse/quorum failures re-tried
    /// automatically without needing an admin to do anything differently, and NotApplicable
    /// affirmatively means "this does not affect you". <see cref="IsNotifiable"/>'s own doc comment
    /// has the full reasoning for why NeedsAttention joined this set. One notification failing to send
    /// must not stop the others in the same batch, matching the per-item posture used throughout this
    /// plugin.
    ///
    /// Final whole-branch review, Finding I4 (Important): notifiable is necessary but no longer
    /// sufficient - <see cref="ShouldNotify"/> also requires the entry's state to be new or changed
    /// since <paramref name="priorStates"/> was snapshotted, so a stuck advisory announces itself once
    /// rather than every hour forever. See that method's own doc comment.
    /// </summary>
    async Task NotifyAsync(
        IReadOnlyList<LedgerEntry> recorded, IReadOnlyDictionary<string, NotifiedState> priorStates)
    {
        foreach (var entry in recorded)
        {
            if (!ShouldNotify(entry, priorStates))
                continue;

            try
            {
                await notificationSender.SendNotification(new AdminScope(), new SecSwitchNotification
                {
                    AdvisoryId = entry.AdvisoryId,
                    Title = entry.Title,
                    Severity = entry.Severity,
                    Outcome = entry.Reason,
                    // Acted is the only status here that is genuinely "Handled" - NeedsDecision and
                    // NeedsAttention both mean an admin needs to look at this, just for different
                    // reasons (an action is computed and held open, vs. SecSwitch could not tell
                    // whether/how to act at all, or tried and failed) - Outcome (entry.Reason) already
                    // carries the specific distinguishing text (PolicyResolver.SshVerificationPendingPhrase,
                    // AdvisoryApplicability.IndeterminateVersionPhrase, an ActionExecutor refusal or
                    // failure message, or a genuine manual-mode/pin/severity-gate hold), so the boolean
                    // here only needs to pick the right prefix. Finding C1 is what makes the "Acted
                    // means handled" half of that true rather than aspirational: a failed action is now
                    // recorded as NeedsAttention, so it takes the "Action required" prefix here instead
                    // of being announced as "Handled".
                    NeedsDecision = entry.Status is LedgerStatus.NeedsDecision or LedgerStatus.NeedsAttention
                });
            }
            catch (Exception e)
            {
                logger.LogError(e, "SecSwitch failed to send an admin notification for advisory {Id}", entry.AdvisoryId);
            }
        }
    }

    /// <summary>
    /// Task 13 review round 2, Finding R1 (Important) fix, extracted as its own directly-testable
    /// method (mirroring <see cref="BuildKnownContentHashes"/>'s own Finding I4 precedent): true for
    /// any status an admin should be notified about.
    ///
    /// <see cref="LedgerStatus.NeedsAttention"/> joined <see cref="LedgerStatus.Acted"/> and
    /// <see cref="LedgerStatus.NeedsDecision"/> here because deferring a fixable CORE advisory while
    /// SSH is configured but not yet verified (see <see cref="PolicyResolver.SshVerificationPendingPhrase"/>)
    /// can persist INDEFINITELY: <c>CheckConfigurationHostedService.TestConnection</c> never gives up -
    /// on failure it retries forever with backoff capped at 10 minutes and simply never sets
    /// <c>CanUseSSH</c> true. Before this fix, an admin with a rotated SSH key, a wrong host, or
    /// container networking trouble would get no bell notification and no alert-banner entry for that
    /// advisory - ever - leaving it discoverable only in an audit log nobody is watching, while the
    /// server keeps running unpatched against an advisory that, before the Finding I2 deferral existed,
    /// would at least have stopped the server outright. Surfacing NeedsAttention from the FIRST poll it
    /// is recorded on (not the Nth, not never) is what makes the deferral's silence acceptable instead
    /// of a silent fail-open - the fix is deliberately NOT "fall back to ShutdownCore after N polls",
    /// which would just reintroduce the exact stop-the-server hazard the deferral exists to remove.
    ///
    /// This also closes the identical, pre-existing silence for the indeterminate-installed-version
    /// case (<see cref="Services.AdvisoryApplicability.IndeterminateVersionPhrase"/>), which has shared
    /// this same status - and therefore this same lack of notification - since the status was
    /// introduced; that was never specific to SSH.
    ///
    /// Rejected and Unverified are deliberately still excluded: both are re-tried automatically without
    /// requiring any admin action, unlike NeedsAttention's core-vs-plugin, indefinitely-stuck
    /// possibility. NotApplicable is excluded because it affirmatively means "this does not affect
    /// you" - nothing for an admin to look at.
    /// </summary>
    internal static bool IsNotifiable(string status) =>
        status is LedgerStatus.Acted or LedgerStatus.NeedsDecision or LedgerStatus.NeedsAttention;

    /// <summary>
    /// The <see cref="LedgerEntry.Status"/>/<see cref="LedgerEntry.Reason"/> pair an advisory held
    /// BEFORE this poll re-recorded it - the only two fields
    /// <see cref="ShouldNotify"/> compares. A snapshot by value, deliberately: it is taken from the
    /// same <see cref="SecSwitchLedger"/> instance <see cref="LedgerStore.RecordAsync"/> may later
    /// mutate in place, so holding the <see cref="LedgerEntry"/> objects themselves would compare an
    /// entry against its own updated self and never report a change.
    /// </summary>
    internal readonly record struct NotifiedState(string Status, string Reason);

    /// <summary>
    /// Snapshots every ledger entry's notification-relevant state, keyed by advisory id
    /// (OrdinalIgnoreCase, matching <see cref="LedgerStore"/>'s own convention). Must be called
    /// BEFORE <see cref="SecSwitchMonitor.ProcessAsync"/> runs - afterwards the prior state is gone.
    /// </summary>
    internal static Dictionary<string, NotifiedState> BuildPriorNotificationStates(SecSwitchLedger ledgerState)
    {
        var snapshot = new Dictionary<string, NotifiedState>(StringComparer.OrdinalIgnoreCase);
        if (ledgerState?.Entries is null)
            return snapshot;

        foreach (var pair in ledgerState.Entries)
            snapshot[pair.Key] = new NotifiedState(pair.Value?.Status ?? "", pair.Value?.Reason ?? "");
        return snapshot;
    }

    /// <summary>
    /// Final whole-branch review, Finding I4 (Important): true only when
    /// <paramref name="entry"/> is notifiable AND its state is actually NEW - either the advisory had
    /// no ledger row before this poll, or its status or reason changed. A notifiable-but-non-terminal
    /// status (in practice <see cref="LedgerStatus.NeedsAttention"/>) is deliberately NOT latched by
    /// <see cref="LedgerStore.IsActedAsync"/>, so it is re-evaluated and re-recorded on every poll -
    /// and the causes that produce it can persist indefinitely (an SSH probe that never succeeds, an
    /// installed version that stays indeterminate, an action that keeps failing to queue). Before
    /// this check, each of those sent a fresh notification EVERY hour, and core's
    /// <c>NotificationSender</c> inserts one row per admin per call with no dedupe of its own: an
    /// unbounded notification table plus a bell an admin quickly learns to ignore, degrading the very
    /// signal the NeedsAttention notification was added to provide.
    ///
    /// Only the notification is suppressed. The entry is still recorded on every poll, so the audit
    /// log and the alert banner (which read the ledger directly, not this method) keep showing it for
    /// as long as it persists - a stuck advisory stays visible, it just stops re-announcing itself.
    /// Reason is compared ordinally and in full: a changed reason means something about the situation
    /// genuinely moved (e.g. an indeterminate version became an SSH deferral), which is worth
    /// re-announcing. That is also why the id-mismatch entry's reason is deliberately free of the
    /// attacker-rotatable index id - see SecSwitchMonitor's mismatch branch.
    /// </summary>
    internal static bool ShouldNotify(LedgerEntry entry, IReadOnlyDictionary<string, NotifiedState> priorStates)
    {
        if (entry is null || !IsNotifiable(entry.Status))
            return false;
        if (priorStates is null || !priorStates.TryGetValue(entry.AdvisoryId ?? "", out var prior))
            return true; // Never recorded before this poll - always announce it.

        return !string.Equals(prior.Status, entry.Status, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(prior.Reason, entry.Reason ?? "", StringComparison.Ordinal);
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
