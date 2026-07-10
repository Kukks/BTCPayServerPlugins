using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.LNURLVerify;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LNURLVerifyPersistenceTests
{
    static string Uniq(string p) => p + Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public async Task Load_restores_non_expired_and_skips_expired()
    {
        var settings = new FakeSettings();
        var live = Uniq("plive_");
        var expired = Uniq("pexp_");
        // Pre-store a self-contained snapshot (not via SaveAsync, to avoid capturing parallel tests' entries).
        var snapshot = new PersistedTrackedInvoices
        {
            Invoices = new()
            {
                new PersistedInvoice { PaymentHash = live, Bolt11 = "lnbc1", VerifyUrl = $"https://h.example/verify/{live}", VerifyHost = "h.example", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
                new PersistedInvoice { PaymentHash = expired, Bolt11 = "lnbc1", VerifyUrl = $"https://h.example/verify/{expired}", VerifyHost = "h.example", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() },
            }
        };
        await settings.UpdateSetting(snapshot, LNURLVerifyPersistence.SettingName);

        await new LNURLVerifyPersistence(settings).LoadAsync();

        Assert.True(TrackedInvoiceRegistry.TryGet(live, out var restored));
        Assert.Equal("lnbc1", restored.Bolt11);
        Assert.Equal($"https://h.example/verify/{live}", restored.VerifyUrl);
        Assert.False(TrackedInvoiceRegistry.TryGet(expired, out _)); // expired -> not re-armed

        TrackedInvoiceRegistry.Remove(live);
    }

    [Fact]
    public async Task Save_writes_the_tracked_invoice_to_settings()
    {
        var settings = new FakeSettings();
        var hash = Uniq("psave_");
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://h.example/verify/{hash}", "h.example", "https://h.example/pay",
            DateTimeOffset.UtcNow.AddHours(1)));

        await new LNURLVerifyPersistence(settings).SaveAsync();

        var stored = await settings.GetSettingAsync<PersistedTrackedInvoices>(LNURLVerifyPersistence.SettingName);
        Assert.NotNull(stored);
        Assert.Contains(stored!.Invoices, i => i.PaymentHash == hash && i.Bolt11 == "lnbc1");

        TrackedInvoiceRegistry.Remove(hash);
    }
}

/// <summary>In-memory ISettingsRepository that round-trips through JSON (like the real one) for tests.</summary>
sealed class FakeSettings : ISettingsRepository
{
    private readonly Dictionary<string, string> _store = new();

    public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        => Task.FromResult(_store.TryGetValue(name ?? typeof(T).FullName!, out var v)
            ? Newtonsoft.Json.JsonConvert.DeserializeObject<T>(v)
            : null);

    public Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
        _store[name ?? typeof(T).FullName!] = Newtonsoft.Json.JsonConvert.SerializeObject(obj);
        return Task.CompletedTask;
    }

    public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        => throw new NotImplementedException();
}
