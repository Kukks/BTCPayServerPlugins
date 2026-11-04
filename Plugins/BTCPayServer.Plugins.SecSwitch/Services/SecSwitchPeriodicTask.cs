using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public sealed class SecSwitchPeriodicTask(
    ISettingsRepository settingsRepository,
    IHttpClientFactory httpClientFactory,
    SecSwitchMonitor monitor,
    LedgerStore ledger,
    ActionExecutor executor,
    LockdownGate lockdown,
    NotificationSender notificationSender,
    IEnumerable<IBTCPayServerPlugin> installedPlugins,
    BTCPayServerEnvironment environment,
    IActionSink sink,
    ILogger<SecSwitchPeriodicTask> logger) : IPeriodicTask
{
    public const string HttpClientName = "SecSwitch";

    static readonly DateTimeOffset ProcessStartedAt = StartOfThisProcess();

    public async Task Do(CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetSettingAsync<SecSwitchSettings>();
        if (settings is not { Enabled: true })
            return;

        var state = BuildState(installedPlugins, environment.Version, sink.CanUpdateCore);
        var recorded = new List<LedgerEntry>();
        var restartNeeded = false;
        var coreUpdateDispatched = false;

        var pollError = await Run("advisory poll", () => PollAdvisoriesAsync(settings, state, cancellationToken));
        if (settings.AutoApply && settings.AutoUpdateAll)
            await Run("auto-update", () => monitor.AutoUpdateAsync(state, cancellationToken));

        await lockdown.RefreshAsync();
        await RecordPollAsync(pollError);
        await NotifyAsync(recorded);

        // A core update restarts BTCPay Server itself and applies queued plugin changes; stopping the process
        // while it runs could kill the update. If it never restarts the server, the changes are still pending
        // on a later poll and the restart is requested then.
        if ((restartNeeded || await HasPendingPluginChangesAsync()) && !coreUpdateDispatched)
            executor.RequestRestart();

        async Task<Exception?> Run(string name, Func<Task<ProcessResult>> step)
        {
            try
            {
                var result = await step();
                recorded.AddRange(result.Recorded);
                restartNeeded |= result.RestartNeeded;
                coreUpdateDispatched |= result.CoreUpdateDispatched;
                return null;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "SecSwitch {Step} failed", name);
                return e;
            }
        }
    }

    async Task<ProcessResult> PollAdvisoriesAsync(SecSwitchSettings settings, InstanceState state, CancellationToken ct)
    {
        if (!AdvisoryFeed.IsSupportedFeedUrl(settings.FeedUrl))
            throw new InvalidOperationException(
                $"The advisory feed URL is not usable ({ActionExecutor.Sanitize(settings.FeedUrl)}): it must be an absolute https:// URL.");

        var feed = await AdvisoryFeed.FetchAsync(httpClientFactory.CreateClient(HttpClientName), settings.FeedUrl, ct);
        foreach (var skipped in feed.Skipped)
            logger.LogWarning("SecSwitch skipped a malformed feed entry: {Reason}", ActionExecutor.Sanitize(skipped.Reason));

        return await monitor.ProcessAsync(feed.Advisories, feed.Skipped.Select(s => s.Id), state, settings, ct);
    }

    async Task<bool> HasPendingPluginChangesAsync()
    {
        try
        {
            return SecSwitchMonitor.HasPendingChanges(
                sink.PendingPluginIdentifiers(), (await ledger.GetAsync()).Entries.Values, ProcessStartedAt);
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not tell whether plugin changes are still waiting for a restart");
            return false;
        }
    }

    async Task RecordPollAsync(Exception? error)
    {
        try
        {
            await ledger.RecordPollAsync(DateTimeOffset.UtcNow, error is null ? null : ActionExecutor.Sanitize(error.Message));
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not record the outcome of its poll");
        }
    }

    async Task NotifyAsync(IEnumerable<LedgerEntry> recorded)
    {
        foreach (var entry in recorded.Where(e => e.Status is LedgerStatus.Acted or LedgerStatus.NeedsDecision or LedgerStatus.NeedsAttention))
        {
            try
            {
                await notificationSender.SendNotification(new AdminScope(), new SecSwitchNotification
                {
                    AdvisoryId = entry.AdvisoryId,
                    Title = entry.Title,
                    Severity = entry.Severity,
                    Outcome = entry.Reason,
                    NeedsDecision = entry.Status is LedgerStatus.NeedsDecision or LedgerStatus.NeedsAttention
                });
            }
            catch (Exception e)
            {
                logger.LogError(e, "SecSwitch failed to send an admin notification for {Id}", entry.AdvisoryId);
            }
        }
    }

    static DateTimeOffset StartOfThisProcess()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime());
    }

    public static InstanceState BuildState(IEnumerable<IBTCPayServerPlugin> plugins, string? coreVersion, bool canUpdateCore)
    {
        var installed = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            if (!string.IsNullOrEmpty(plugin.Identifier))
                installed[plugin.Identifier] = plugin.Version;
        }

        // "v2.4.7+abc123" and "2.4.7-rc1" both reduce to 2.4.7; anything else is unknown, never 0.0,
        // because a 0.0 core would match every "<x" range and lock the server down.
        var parsed = Version.TryParse((coreVersion ?? "").TrimStart('v').Split('+', '-')[0], out var version) ? version : null;
        return new InstanceState(installed, parsed, canUpdateCore);
    }
}
