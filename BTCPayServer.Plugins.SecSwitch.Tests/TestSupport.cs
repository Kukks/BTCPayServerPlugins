using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// <summary>Round-trips every value through JSON like core's SettingsRepository, so no test depends on object aliasing.</summary>
public sealed class FakeSettingsRepository : ISettingsRepository
{
    readonly Dictionary<string, string> _store = new();
    public bool RejectLedgerWrites { get; set; }
    public Func<SecSwitchLedger, bool>? RejectLedgerWrite { get; set; }

    public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        => Task.FromResult(_store.TryGetValue(name ?? typeof(T).FullName!, out var json) ? JsonConvert.DeserializeObject<T>(json) : null);

    public Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
        if (obj is SecSwitchLedger ledger && (RejectLedgerWrites || RejectLedgerWrite?.Invoke(ledger) == true))
            throw new InvalidOperationException("Ledger temporarily unavailable");
        _store[name ?? typeof(T).FullName!] = JsonConvert.SerializeObject(obj);
        return Task.CompletedTask;
    }

    public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        => throw new NotSupportedException();
}

public sealed class FakeSink : IActionSink
{
    public List<string> Disabled { get; } = [];
    public List<(string Identifier, string Version)> Updated { get; } = [];
    public bool CanUpdateCore { get; set; }
    public int CoreUpdates { get; private set; }
    public int Stops { get; private set; }

    public bool QueueDisableResult { get; set; } = true;
    public bool QueueUpdateResult { get; set; } = true;
    public bool VerifiedQueuedUpdate { get; set; }
    public Exception? QueueUpdateThrows { get; set; }
    public Exception? CoreUpdateThrows { get; set; }
    public Func<Task>? BeforePluginQueue { get; set; }
    public Func<Task>? BeforeCoreUpdate { get; set; }
    public Action? OnReadLatestCore { get; set; }
    public List<PluginUpdate> PluginUpdates { get; set; } = [];
    public Version? LatestCore { get; set; }
    public List<string> Pending { get; set; } = [];

    public bool QueueDisable(string identifier)
    {
        Disabled.Add(identifier);
        return QueueDisableResult;
    }

    public async Task<PluginUpdateQueueResult> QueueUpdateAsync(string identifier, string version,
        Func<Func<Task<bool>>, Task<bool>>? commitUpdate = null)
    {
        if (BeforePluginQueue is not null)
            await BeforePluginQueue();
        if (QueueUpdateThrows is not null)
            throw QueueUpdateThrows;
        if (!QueueUpdateResult)
            return PluginUpdateQueueResult.Unavailable;

        Task<bool> Queue()
        {
            Updated.Add((identifier, version));
            return Task.FromResult(true);
        }
        return (commitUpdate is null ? await Queue() : await commitUpdate(Queue))
            ? PluginUpdateQueueResult.Queued
            : PluginUpdateQueueResult.PolicyChanged;
    }

    public async Task TriggerCoreUpdateAsync()
    {
        if (BeforeCoreUpdate is not null)
            await BeforeCoreUpdate();
        if (CoreUpdateThrows is not null)
            throw CoreUpdateThrows;
        CoreUpdates++;
    }

    public void StopApplication() => Stops++;

    public IReadOnlyCollection<string> PendingPluginIdentifiers() => Pending;
    public bool HasVerifiedQueuedUpdate(string identifier, string version) => VerifiedQueuedUpdate;

    public Task<IReadOnlyList<PluginUpdate>> FindPluginUpdatesAsync(
        IReadOnlyDictionary<string, Version> installed, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PluginUpdate>>(PluginUpdates);

    public Task<Version?> GetLatestCoreVersionAsync(CancellationToken ct)
    {
        OnReadLatestCore?.Invoke();
        return Task.FromResult(LatestCore);
    }
}

public static class Fixtures
{
    public static Advisory Adv(
        string id = "adv-1", string identifier = "Plug", string affected = ">=1.0.0 && <1.2.3",
        string? fixedVersion = null, AdvisorySeverity severity = AdvisorySeverity.High) => new()
    {
        Id = id, Identifier = identifier, AffectedVersions = affected, FixedVersion = fixedVersion,
        Severity = severity, Title = $"Title of {id}"
    };

    public static InstanceState State(
        string pluginVersion = "1.1.0", string? coreVersion = "2.4.7", bool canUpdateCore = false, string pluginId = "Plug") =>
        new(new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase) { [pluginId] = Version.Parse(pluginVersion) },
            coreVersion is null ? null : Version.Parse(coreVersion), canUpdateCore);

    public static SecSwitchSettings Settings(
        bool enabled = true, bool autoApply = true, bool autoUpdatePlugin = false, bool autoUpdateCore = false) => new()
    {
        Enabled = enabled, AutoApply = autoApply, AutoUpdateCore = autoUpdateCore, AutoUpdatePlugins = autoUpdatePlugin ? ["Plug"] : []
    };
}
