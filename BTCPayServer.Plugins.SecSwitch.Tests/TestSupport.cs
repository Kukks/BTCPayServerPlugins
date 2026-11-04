using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// <summary>Round-trips every value through JSON like core's SettingsRepository, so no test depends on object aliasing.</summary>
public sealed class FakeSettingsRepository : ISettingsRepository
{
    readonly Dictionary<string, string> _store = new();

    public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        => Task.FromResult(_store.TryGetValue(name ?? typeof(T).FullName!, out var json) ? JsonConvert.DeserializeObject<T>(json) : null);

    public Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
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
    public Exception? QueueUpdateThrows { get; set; }
    public Exception? CoreUpdateThrows { get; set; }
    public List<PluginUpdate> PluginUpdates { get; set; } = [];
    public Version? LatestCore { get; set; }

    public bool QueueDisable(string identifier)
    {
        Disabled.Add(identifier);
        return QueueDisableResult;
    }

    public Task<bool> QueueUpdateAsync(string identifier, string version)
    {
        if (QueueUpdateThrows is not null)
            throw QueueUpdateThrows;
        Updated.Add((identifier, version));
        return Task.FromResult(QueueUpdateResult);
    }

    public Task TriggerCoreUpdateAsync()
    {
        if (CoreUpdateThrows is not null)
            throw CoreUpdateThrows;
        CoreUpdates++;
        return Task.CompletedTask;
    }

    public void StopApplication() => Stops++;

    public Task<IReadOnlyList<PluginUpdate>> FindPluginUpdatesAsync(
        IReadOnlyDictionary<string, Version> installed, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PluginUpdate>>(PluginUpdates);

    public Task<Version?> GetLatestCoreVersionAsync(CancellationToken ct) => Task.FromResult(LatestCore);
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
        bool enabled = true, bool autoApply = true, bool preferUpdate = true, bool autoUpdateAll = false) => new()
    {
        Enabled = enabled, AutoApply = autoApply, PreferUpdateOverDisable = preferUpdate, AutoUpdateAll = autoUpdateAll
    };
}
