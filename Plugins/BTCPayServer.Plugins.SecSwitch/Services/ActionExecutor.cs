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
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public sealed record ActionOutcome(bool Succeeded, string Message, bool RestartNeeded = false, bool AlreadyQueued = false);

public sealed record PluginUpdate(string Identifier, Version Available);

public enum PluginUpdateQueueResult { Unavailable, PolicyChanged, Queued, AlreadyQueued }

public interface IActionSink
{
    /// <summary>True when the host integration (<c>btcpay-host</c>) offers the <c>update</c> command.</summary>
    bool CanUpdateCore { get; }
    /// <summary>Plugins with a queued disable, install or delete that only a restart applies.</summary>
    IReadOnlyCollection<string> PendingPluginIdentifiers();
    bool QueueDisable(string identifier);
    bool HasVerifiedQueuedUpdate(string identifier, string version);
    Task<PluginUpdateQueueResult> QueueUpdateAsync(string identifier, string version,
        Func<Func<Task<bool>>, Task<bool>>? commitUpdate = null);
    Task TriggerCoreUpdateAsync();
    void StopApplication();
    Task<IReadOnlyList<PluginUpdate>> FindPluginUpdatesAsync(IReadOnlyDictionary<string, Version> installed, CancellationToken ct);
    Task<Version?> GetLatestCoreVersionAsync(CancellationToken ct);
}

public sealed class ActionExecutor(IActionSink sink, ILogger<ActionExecutor> logger)
{
    static readonly Regex SafeIdentifierPattern = new(@"\A[A-Za-z0-9._-]{1,128}\z", RegexOptions.Compiled);

    public async Task<ActionOutcome> ExecuteAsync(SecSwitchAction action, Advisory advisory, bool allowDisableFallback = true,
        Func<Func<Task<bool>>, Task<bool>>? commitUpdate = null, Func<Func<ActionOutcome>, Task<ActionOutcome>>? commitDisable = null)
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
                    return await DisablePluginAsync(advisory.Identifier, commitDisable);
                case SecSwitchAction.UpdatePlugin:
                    return await UpdateOrDisableAsync(advisory, allowDisableFallback, commitUpdate, commitDisable);
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

    public async Task<ActionOutcome> UpdatePluginAsync(string identifier, string version,
        Func<Func<Task<bool>>, Task<bool>>? commitUpdate = null)
    {
        if (!IsSafeIdentifier(identifier))
            return new ActionOutcome(false, $"Refusing to update: plugin identifier '{Sanitize(identifier)}' is missing or unsafe.");
        try
        {
            var target = $"{Sanitize(identifier)} {Sanitize(version)}";
            return await sink.QueueUpdateAsync(identifier, version, commitUpdate) switch
            {
                PluginUpdateQueueResult.Queued => new ActionOutcome(true, $"Queued update to {target}.", RestartNeeded: true),
                PluginUpdateQueueResult.AlreadyQueued => new ActionOutcome(true, $"The update to {target} is already queued. Restart BTCPay Server to apply it.", AlreadyQueued: true),
                PluginUpdateQueueResult.PolicyChanged => new ActionOutcome(false, $"Current SecSwitch policy does not authorize the update to {target}."),
                _ => new ActionOutcome(false, $"Failed to queue the update to {target}: no installed plugin matches that identifier unambiguously.")
            };
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

    async Task<ActionOutcome> UpdateOrDisableAsync(Advisory advisory, bool allowDisableFallback,
        Func<Func<Task<bool>>, Task<bool>>? commitUpdate, Func<Func<ActionOutcome>, Task<ActionOutcome>>? commitDisable)
    {
        if (string.IsNullOrWhiteSpace(advisory.FixedVersion))
            return allowDisableFallback
                ? await DisablePluginAsync(advisory.Identifier, commitDisable)
                : new ActionOutcome(false, "No fixed version is available for this advisory.");

        var update = await UpdatePluginAsync(advisory.Identifier, advisory.FixedVersion, commitUpdate);
        if (update.Succeeded || !allowDisableFallback)
            return update;

        var disable = await DisablePluginAsync(advisory.Identifier, commitDisable);
        return disable with { Message = $"{update.Message} Disabling instead: {disable.Message}" };
    }

    Task<ActionOutcome> DisablePluginAsync(string identifier, Func<Func<ActionOutcome>, Task<ActionOutcome>>? commitDisable) =>
        commitDisable is null ? Task.FromResult(DisablePlugin(identifier)) : commitDisable(() => DisablePlugin(identifier));

    ActionOutcome DisablePlugin(string identifier)
    {
        if (!IsSafeIdentifier(identifier))
            return new ActionOutcome(false, $"Refusing to disable: plugin identifier '{Sanitize(identifier)}' is missing or unsafe.");
        return sink.QueueDisable(identifier)
            ? new ActionOutcome(true, $"{Sanitize(identifier)} is queued to be disabled or removed at restart.", RestartNeeded: true)
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
        var pluginDir = dataDirectories.Value.PluginDir;
        if (!TryResolveInstalledDirectory(pluginDir, identifier, out var resolved, out var ambiguous))
        {
            LogUnresolved("disable", identifier, ambiguous);
            return false;
        }
        if (LastPendingChange(PluginManager.GetPendingCommands(pluginDir), resolved, StringComparison.Ordinal) is not ("disable" or "delete"))
            PluginManager.DisablePlugin(pluginDir, resolved);
        return true;
    }

    public bool HasVerifiedQueuedUpdate(string identifier, string version)
    {
        var pluginDir = dataDirectories.Value.PluginDir;
        return Version.TryParse(version, out var expected) &&
               TryResolveInstalledDirectory(pluginDir, identifier, out var resolved, out _) &&
               IsExactFixQueued(pluginDir, resolved, AdvisoryApplicability.Pad(expected), PluginManager.GetPendingCommands(pluginDir));
    }

    public async Task<PluginUpdateQueueResult> QueueUpdateAsync(string identifier, string version,
        Func<Func<Task<bool>>, Task<bool>>? commitUpdate = null)
    {
        var pluginDir = dataDirectories.Value.PluginDir;
        if (!TryResolveInstalledDirectory(pluginDir, identifier, out var resolved, out var ambiguous))
        {
            LogUnresolved("update", identifier, ambiguous);
            return PluginUpdateQueueResult.Unavailable;
        }

        var candidates = UpdateCandidates(version);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"'{ActionExecutor.Sanitize(version)}' is not a valid version.");
        var expectedVersion = Version.Parse(candidates[0]);
        var pending = PluginManager.GetPendingCommands(pluginDir);
        if (IsExactFixQueued(pluginDir, resolved, expectedVersion, pending))
            return PluginUpdateQueueResult.AlreadyQueued;
        EnsureNoPendingChange(pending, resolved);

        using var scope = scopeFactory.CreateScope();
        var staging = Directory.CreateDirectory(Path.Combine(pluginDir, $".secswitch-{Guid.NewGuid():N}"));
        try
        {
            var downloader = ActivatorUtilities.CreateInstance<PluginService>(scope.ServiceProvider,
                Options.Create(new DataDirectories { PluginDir = staging.FullName }));
            var manifest = await DownloadUpdateAsync(downloader, resolved, candidates);
            pending = PluginManager.GetPendingCommands(pluginDir);
            if (IsExactFixQueued(pluginDir, resolved, expectedVersion, pending))
                return PluginUpdateQueueResult.AlreadyQueued;
            EnsureNoPendingChange(pending, resolved);
            EnsureDependencies(manifest, downloader.Installed, pending);

            return await QueueStagedFilesAsync(pluginDir, staging.FullName, resolved, manifest, downloader.Installed, commitUpdate)
                ? PluginUpdateQueueResult.Queued
                : PluginUpdateQueueResult.PolicyChanged;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            if (IsExactFixQueued(pluginDir, resolved, expectedVersion, PluginManager.GetPendingCommands(pluginDir)))
                return PluginUpdateQueueResult.AlreadyQueued;
            throw;
        }
        finally
        {
            try
            {
                staging.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Could not clean up the SecSwitch update staging directory");
            }
        }
    }

    static async Task<PluginService.AvailablePlugin> DownloadUpdateAsync(PluginService downloader, string identifier,
        IReadOnlyList<string> candidates)
    {
        InvalidOperationException? failure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return await downloader.DownloadRemotePlugin(identifier, candidate);
            }
            catch (InvalidOperationException e)
            {
                failure = e;
            }
        }
        throw failure ?? new InvalidOperationException("No matching plugin package is available.");
    }

    async Task<bool> QueueStagedFilesAsync(string pluginDir, string stagingDir, string identifier,
        PluginService.AvailablePlugin update, Dictionary<string, Version> installed, Func<Func<Task<bool>>, Task<bool>>? commitUpdate)
    {
        var packageName = identifier + PluginManager.BTCPayPluginSuffix;
        var manifestName = identifier + ".json";
        var packagePath = Path.Combine(pluginDir, packageName);
        var manifestPath = Path.Combine(pluginDir, manifestName);
        // Keep the live files locked through queueing. Delete sharing lets us clean up our own files on failure.
        await using var package = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.Delete);
        FileStream? manifest = null;
        var queued = false;
        try
        {
            manifest = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Delete);
            await CopyStagedAsync(packageName, package);
            await CopyStagedAsync(manifestName, manifest);
            if (commitUpdate is null)
                await QueueAsync();
            else
                await commitUpdate(QueueAsync);
            return queued;
        }
        finally
        {
            if (!queued)
            {
                if (manifest is not null)
                    DeleteStagedFile(manifestPath);
                DeleteStagedFile(packagePath);
            }
            if (manifest is not null)
                await manifest.DisposeAsync();
        }

        async Task CopyStagedAsync(string name, FileStream destination)
        {
            await using var source = File.OpenRead(Path.Combine(stagingDir, name));
            await source.CopyToAsync(destination);
            await destination.FlushAsync();
        }

        Task<bool> QueueAsync()
        {
            var pending = PluginManager.GetPendingCommands(pluginDir);
            EnsureNoPendingChange(pending, identifier);
            EnsureDependencies(update, installed, pending);
            PluginManager.QueueCommands(pluginDir, ("install", identifier));
            queued = true;
            return Task.FromResult(true);
        }
    }

    void DeleteStagedFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not clean up a staged SecSwitch update file");
        }
    }

    static string? LastPendingChange(IEnumerable<(string command, string plugin)> pending, string identifier, StringComparison comparison) =>
        pending.LastOrDefault(command => string.Equals(command.plugin, identifier, comparison)).command;

    static bool IsExactFixQueued(string pluginDir, string identifier, Version expectedVersion,
        IEnumerable<(string command, string plugin)> pending)
    {
        if (LastPendingChange(pending, identifier, StringComparison.Ordinal) != "install" ||
            !File.Exists(Path.Combine(pluginDir, identifier + PluginManager.BTCPayPluginSuffix)))
            return false;

        try
        {
            var manifest = JsonConvert.DeserializeObject<PluginService.AvailablePlugin>(
                File.ReadAllText(Path.Combine(pluginDir, identifier + ".json")));
            return manifest is { Version: { } version } && string.Equals(manifest.Identifier, identifier, StringComparison.Ordinal) &&
                   AdvisoryApplicability.Pad(version) == expectedVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    static void EnsureNoPendingChange(IEnumerable<(string command, string plugin)> pending, string identifier)
    {
        if (LastPendingChange(pending, identifier, StringComparison.OrdinalIgnoreCase) is not null)
            throw new InvalidOperationException("A change for this plugin is already queued.");
    }

    static void EnsureDependencies(PluginService.AvailablePlugin manifest, Dictionary<string, Version> installed,
        IEnumerable<(string command, string plugin)> pending)
    {
        if (!CanInstallUpdate(manifest, installed, pending.Select(command => command.plugin)))
            throw new InvalidOperationException("The update's dependencies are missing, incompatible, or have pending changes.");
    }

    public static bool CanInstallUpdate(PluginService.AvailablePlugin manifest, Dictionary<string, Version> installed,
        IEnumerable<string> pendingIdentifiers)
    {
        var pending = pendingIdentifiers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return manifest.Dependencies is { } dependencies &&
               dependencies.All(d => d is not null && !string.IsNullOrWhiteSpace(d.Identifier) && !pending.Contains(d.Identifier)) &&
               PluginManager.DependenciesMet(dependencies, installed);
    }

    /// <summary>The download needs an exact match and the plugin builder publishes four-part versions.</summary>
    public static IReadOnlyList<string> UpdateCandidates(string version)
    {
        if (!Version.TryParse(version, out var parsed))
            return [];
        var padded = AdvisoryApplicability.Pad(parsed).ToString();
        return padded == parsed.ToString() ? [padded] : [padded, parsed.ToString()];
    }

    public IReadOnlyCollection<string> PendingPluginIdentifiers() =>
        PluginManager.GetPendingCommands(dataDirectories.Value.PluginDir).Select(command => command.plugin).ToList();

    public async Task<IReadOnlyList<PluginUpdate>> FindPluginUpdatesAsync(
        IReadOnlyDictionary<string, Version> installed, CancellationToken ct)
    {
        // A queued disable or install is applied at the next start, and "install" re-enables the plugin first,
        // so a plugin with a pending command must not be queued again.
        var pending = PendingPluginIdentifiers().ToHashSet(StringComparer.OrdinalIgnoreCase);
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
