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
        var settings = await ledger.GetPolicyAsync();
        if (!settings.Enabled)
        {
            await lockdown.RefreshAsync();
            return;
        }
        await monitor.RetryPendingAuditAsync();
        if (settings.GetFeedUrl() is not { } url)
        {
            await lockdown.RefreshAsync();
            if (!cancellationToken.IsCancellationRequested && !await lockdown.IsActiveAsync() && await HasPendingPluginChangesAsync())
                executor.RequestRestart();
            return;
        }

        var state = BuildState(installedPlugins, environment.Version, sink.CanUpdateCore);
        var restartNeeded = false;
        var coreUpdateMayBeRunning = false;

        FeedResult? feed = null;
        var pollError = await Run("advisory poll", async () =>
        {
            feed = await FetchAdvisoriesAsync(url, cancellationToken);
            var current = await ledger.GetPolicyAsync();
            if (!current.Enabled || !string.Equals(current.GetFeedUrl(), url, StringComparison.Ordinal))
            {
                feed = null;
                return new ProcessResult([], false);
            }
            settings = current;
            return await monitor.ProcessAsync(feed.Advisories, feed.Skipped.Select(s => s.Id), state, settings, cancellationToken);
        });
        if (pollError is null && feed is { Skipped.Count: 0 })
        {
            await Run("auto-update", async () =>
            {
                var current = await ledger.GetPolicyAsync();
                if (!current.Enabled || !string.Equals(current.GetFeedUrl(), url, StringComparison.Ordinal) ||
                    current.PolicyRevision != settings.PolicyRevision ||
                    (!current.AutoUpdateCore && current.AutoUpdatePlugins is not { Length: > 0 }))
                    return new ProcessResult([], false);
                return await monitor.AutoUpdateAsync(state, current, feed.Advisories, cancellationToken);
            });
        }

        await lockdown.RefreshAsync();
        if (pollError is not null || feed is not null)
            await RecordPollAsync(pollError, url, settings.PolicyRevision, feed?.Skipped.Count ?? 0);
        await monitor.RetryPendingAuditAsync();
        await NotifyAsync();

        // A core update restarts BTCPay Server itself and applies queued plugin changes; stopping the process
        // while it runs could kill the update. If it never restarts the server, the changes are still pending
        // on a later poll and the restart is requested then.
        if (!coreUpdateMayBeRunning && !cancellationToken.IsCancellationRequested && !await lockdown.IsActiveAsync() &&
            (restartNeeded || await HasPendingPluginChangesAsync()))
            executor.RequestRestart();

        async Task<Exception?> Run(string name, Func<Task<ProcessResult>> step)
        {
            try
            {
                var result = await step();
                restartNeeded |= result.RestartNeeded;
                coreUpdateMayBeRunning |= result.CoreUpdateDispatched || result.CoreUpdateMayBeRunning;
                return null;
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "SecSwitch {Step} failed", name);
                return e;
            }
        }
    }

    async Task<FeedResult> FetchAdvisoriesAsync(string url, CancellationToken ct)
    {
        var feed = await AdvisoryFeed.FetchAsync(httpClientFactory.CreateClient(HttpClientName), url, ct);
        foreach (var skipped in feed.Skipped)
            logger.LogWarning("SecSwitch skipped a malformed feed entry: {Reason}", ActionExecutor.Sanitize(skipped.Reason));

        return feed;
    }

    async Task<bool> HasPendingPluginChangesAsync()
    {
        try
        {
            var pending = sink.PendingPluginIdentifiers();
            if (SecSwitchMonitor.HasPendingChanges(pending, monitor.GetPendingPluginChangeEntries(pending), ProcessStartedAt))
                return true;
            return SecSwitchMonitor.HasPendingChanges(
                pending, (await ledger.GetAsync()).Entries.Values, ProcessStartedAt);
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not tell whether plugin changes are still waiting for a restart");
            return false;
        }
    }

    async Task RecordPollAsync(Exception? error, string url, Guid? policyRevision, int skippedEntries)
    {
        try
        {
            await ledger.RecordPollAsync(DateTimeOffset.UtcNow, error is null ? null : ActionExecutor.Sanitize(error.Message),
                url, policyRevision, skippedEntries);
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not record the outcome of its poll");
        }
    }

    async Task NotifyAsync()
    {
        IReadOnlyList<LedgerEntry> pending;
        try
        {
            pending = (await ledger.GetAsync()).Entries.Values.Where(e => e.NotificationPending && !e.Suppressed).ToList();
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch could not load pending admin notifications");
            return;
        }
        foreach (var entry in pending)
        {
            try
            {
                var entries = (await ledger.GetAsync()).Entries;
                if (!entries.TryGetValue(entry.AdvisoryId, out var current) ||
                    current.Suppressed || !current.NotificationPending || current.RecordedAt != entry.RecordedAt)
                    continue;
                await notificationSender.SendNotification(new AdminScope(), new SecSwitchNotification
                {
                    AdvisoryId = entry.AdvisoryId,
                    Title = entry.Title,
                    Severity = entry.Severity,
                    Outcome = entry.Reason,
                    NeedsDecision = entry.Status is LedgerStatus.NeedsDecision or LedgerStatus.NeedsAttention
                });
                await ledger.MarkNotifiedAsync(entry.AdvisoryId, entry.RecordedAt);
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
