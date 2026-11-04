using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Maintenance;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public sealed record ActionOutcome(bool Succeeded, string Message, bool RestartNeeded = false);

public sealed record PluginUpdate(string Identifier, Version Available);

public interface IActionSink
{
    /// <summary>True when the host integration (<c>btcpay-host</c>) offers the <c>update</c> command.</summary>
    bool CanUpdateCore { get; }
    bool QueueDisable(string identifier);
    Task<bool> QueueUpdateAsync(string identifier, string version);
    Task TriggerCoreUpdateAsync();
    void StopApplication();
    Task<IReadOnlyList<PluginUpdate>> FindPluginUpdatesAsync(IReadOnlyDictionary<string, Version> installed, CancellationToken ct);
    Task<Version?> GetLatestCoreVersionAsync(CancellationToken ct);
}

public sealed class ActionExecutor(IActionSink sink, ILogger<ActionExecutor> logger)
{
    static readonly Regex SafeIdentifierPattern = new(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.Compiled);

    public async Task<ActionOutcome> ExecuteAsync(SecSwitchAction action, Advisory advisory)
    {
        try
        {
            switch (action)
            {
                case SecSwitchAction.None:
                case SecSwitchAction.Notify:
                    return new ActionOutcome(true, "No instance change made.");
                case SecSwitchAction.Lockdown:
                    return new ActionOutcome(true, "Server locked down: only server admins can reach it until the advisory no longer applies.");
                case SecSwitchAction.DisablePlugin:
                    return DisablePlugin(advisory.Identifier);
                case SecSwitchAction.UpdatePlugin:
                    return await UpdateOrDisableAsync(advisory);
                case SecSwitchAction.UpdateCore:
                    return await UpdateCoreAsync();
                default:
                    return new ActionOutcome(false, $"Unknown action {action}; nothing done.");
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch action {Action} for {Identifier} failed", action, Sanitize(advisory.Identifier));
            return new ActionOutcome(false, $"Action {action} failed: {Sanitize(e.Message)}");
        }
    }

    public async Task<ActionOutcome> UpdatePluginAsync(string identifier, string version)
    {
        if (!IsSafeIdentifier(identifier))
            return new ActionOutcome(false, $"Refusing to update: plugin identifier '{Sanitize(identifier)}' is missing or unsafe.");
        try
        {
            return await sink.QueueUpdateAsync(identifier, version)
                ? new ActionOutcome(true, $"Queued update of {Sanitize(identifier)} to {Sanitize(version)}.", RestartNeeded: true)
                : new ActionOutcome(false, $"Failed to queue update of {Sanitize(identifier)}: no installed plugin matches that identifier unambiguously.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch update of {Identifier} to {Version} failed", Sanitize(identifier), Sanitize(version));
            return new ActionOutcome(false, $"Update of {Sanitize(identifier)} failed: {Sanitize(e.Message)}");
        }
    }

    public async Task<ActionOutcome> UpdateCoreAsync()
    {
        try
        {
            await sink.TriggerCoreUpdateAsync();
            return new ActionOutcome(true, "Triggered BTCPay Server update through the host integration.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "SecSwitch core update failed");
            return new ActionOutcome(false, $"Core update failed: {Sanitize(e.Message)}");
        }
    }

    public void RequestRestart() => sink.StopApplication();

    async Task<ActionOutcome> UpdateOrDisableAsync(Advisory advisory)
    {
        if (string.IsNullOrWhiteSpace(advisory.FixedVersion))
            return DisablePlugin(advisory.Identifier);

        var update = await UpdatePluginAsync(advisory.Identifier, advisory.FixedVersion);
        if (update.Succeeded)
            return update;

        var disable = DisablePlugin(advisory.Identifier);
        return disable with { Message = $"{update.Message} Disabling instead: {disable.Message}" };
    }

    ActionOutcome DisablePlugin(string identifier)
    {
        if (!IsSafeIdentifier(identifier))
            return new ActionOutcome(false, $"Refusing to disable: plugin identifier '{Sanitize(identifier)}' is missing or unsafe.");
        return sink.QueueDisable(identifier)
            ? new ActionOutcome(true, $"Queued disable of {Sanitize(identifier)}.", RestartNeeded: true)
            : new ActionOutcome(false, $"Failed to queue disable of {Sanitize(identifier)}: no installed plugin matches that identifier unambiguously.");
    }

    // The identifier becomes a path segment under the plugin directory, so reject anything path-like.
    static bool IsSafeIdentifier(string? identifier) =>
        identifier is not null && SafeIdentifierPattern.IsMatch(identifier) &&
        !identifier.Contains("..", StringComparison.Ordinal);

    public static string Sanitize(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var cleaned = new string(value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)).ToArray());
        return cleaned.Length > maxLength ? cleaned[..maxLength] + "..." : cleaned;
    }
}

public sealed class BtcPayActionSink(
    IServiceScopeFactory scopeFactory,
    ISettingsRepository settingsRepository,
    IOptions<DataDirectories> dataDirectories,
    HostIntegrationState hostIntegration,
    ProcessRunner processRunner,
    BTCPayServerEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<BtcPayActionSink> logger) : IActionSink
{
    static readonly TimeSpan CoreUpdateTimeout = TimeSpan.FromMinutes(20);
    static readonly TimeSpan CoreUpdateStartGrace = TimeSpan.FromSeconds(10);

    public bool CanUpdateCore => hostIntegration.Current?.Commands?.Contains(HostCommands.Update) == true;

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

        var candidates = UpdateCandidates(version);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"'{ActionExecutor.Sanitize(version)}' is not a valid version.");

        using var scope = scopeFactory.CreateScope();
        var pluginService = scope.ServiceProvider.GetRequiredService<PluginService>();
        InvalidOperationException? failure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await pluginService.DownloadRemotePlugin(resolved, candidate);
                failure = null;
                break;
            }
            catch (InvalidOperationException e)
            {
                failure = e;
            }
        }
        if (failure is not null)
            throw failure;

        pluginService.InstallPlugin(resolved);
        return true;
    }

    /// <summary>The download needs an exact match and the plugin builder publishes four-part versions.</summary>
    public static IReadOnlyList<string> UpdateCandidates(string version)
    {
        if (!Version.TryParse(version, out var parsed))
            return [];
        var padded = AdvisoryApplicability.Pad(parsed).ToString();
        return padded == parsed.ToString() ? [padded] : [padded, parsed.ToString()];
    }

    public async Task<IReadOnlyList<PluginUpdate>> FindPluginUpdatesAsync(
        IReadOnlyDictionary<string, Version> installed, CancellationToken ct)
    {
        // A queued disable or install is applied at the next start, and "install" re-enables the plugin first,
        // so a plugin with a pending command must not be queued again.
        var pending = PluginManager.GetPendingCommands(dataDirectories.Value.PluginDir)
            .Select(command => command.plugin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requests = installed
            .Where(kv => !pending.Contains(kv.Key) &&
                         TryResolveInstalledDirectory(dataDirectories.Value.PluginDir, kv.Key, out _, out _))
            .Select(kv => new InstalledPluginRequest(kv.Key, kv.Value.ToString()))
            .ToList();
        if (requests.Count == 0)
            return [];

        var policies = await settingsRepository.GetSettingAsync<PoliciesSettings>() ?? new PoliciesSettings();
        using var scope = scopeFactory.CreateScope();
        var published = await scope.ServiceProvider.GetRequiredService<PluginBuilderClient>().GetInstalledPluginsUpdates(
            environment.Version.TrimStart('v').Split('+')[0], policies.PluginPreReleases, requests, ct);

        return SelectUpdates(published, installed, pending);
    }

    public static IReadOnlyList<PluginUpdate> SelectUpdates(
        IEnumerable<PublishedVersion> published, IReadOnlyDictionary<string, Version> installed, ISet<string>? pending = null)
        => published
            .Select(p => p.ManifestInfo?.ToObject<PluginService.AvailablePlugin>())
            .Where(p => p?.Identifier is not null && p.Version is not null && pending?.Contains(p.Identifier) != true &&
                        installed.TryGetValue(p.Identifier, out var current) &&
                        AdvisoryApplicability.Pad(p.Version) > AdvisoryApplicability.Pad(current))
            .GroupBy(p => p!.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PluginUpdate(g.Key, g.Max(p => p!.Version)!))
            .ToList();

    // Core's own daily release check keeps the latest release it announced here (only when the admin enabled
    // "check for new versions"), so SecSwitch does not poll GitHub itself.
    public async Task<Version?> GetLatestCoreVersionAsync(CancellationToken ct)
    {
        var checkedVersion = await settingsRepository.GetSettingAsync<NewVersionCheckerDataHolder>();
        return Version.TryParse(checkedVersion?.LastVersion, out var version) ? version : null;
    }

    void LogUnresolved(string actionVerb, string identifier, IReadOnlyList<string> ambiguous)
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

    // Plugin directories are matched on-disk-casing-exact first; a single case-insensitive match is
    // accepted, several are refused rather than guessed (case-variant directories can coexist on Linux).
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

        ambiguousMatches = caseInsensitiveMatches;
        return false;
    }

    // The update replaces this very process, so it is not awaited to completion. A command that fails
    // within the grace period (missing executable, non-zero exit) is still reported as a failure.
    // The command is a constant; nothing from an advisory reaches it.
    public async Task TriggerCoreUpdateAsync()
    {
        if (!CanUpdateCore)
            throw new InvalidOperationException("The btcpay-host integration does not offer 'update'; cannot update BTCPay Server.");

        var update = processRunner.RunHostCommand(HostCommands.Update, null, CoreUpdateTimeout);
        if (await Task.WhenAny(update, Task.Delay(CoreUpdateStartGrace)) == update)
        {
            var result = await update;
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"'btcpay-host update' exited with code {result.ExitCode}: {result.Error}");
            logger.LogInformation("SecSwitch's 'btcpay-host update' finished quickly with exit code 0");
            return;
        }

        _ = update.ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger.LogWarning(t.Exception, "SecSwitch's 'btcpay-host update' failed after being dispatched");
            else if (t.IsCompletedSuccessfully && t.Result.ExitCode != 0)
                logger.LogWarning("SecSwitch's 'btcpay-host update' exited with code {ExitCode}: {Error}",
                    t.Result.ExitCode, ActionExecutor.Sanitize(t.Result.Error));
        }, TaskScheduler.Default);
        logger.LogInformation("SecSwitch dispatched 'btcpay-host update'; not waiting for it to finish.");
    }

    public void StopApplication() => lifetime.StopApplication();
}
