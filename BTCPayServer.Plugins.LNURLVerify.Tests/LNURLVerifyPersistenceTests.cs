using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.LNURLVerify;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

[Collection(RegistryCollection.Name)]
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

    [Fact]
    public async Task Save_writes_the_verifyBatch_url()
    {
        var settings = new FakeSettings();
        var hash = Uniq("psvb_");
        TrackedInvoiceRegistry.Add(new TrackedInvoice(hash, "lnbc1", $"https://h.example/verify/{hash}", "h.example",
            "https://h.example/pay", DateTimeOffset.UtcNow.AddHours(1), "https://h.example/lnurl/verifyBatch"));

        await new LNURLVerifyPersistence(settings).SaveAsync();

        var stored = await settings.GetSettingAsync<PersistedTrackedInvoices>(LNURLVerifyPersistence.SettingName);
        Assert.Contains(stored!.Invoices, i => i.PaymentHash == hash && i.VerifyBatch == "https://h.example/lnurl/verifyBatch");
        TrackedInvoiceRegistry.Remove(hash);
    }

    [Fact]
    public async Task Load_restores_verifyBatch_and_accepts_a_v1_0_blob_without_it()
    {
        var settings = new FakeSettings();
        var withBatch = Uniq("plvb_");
        var legacy = Uniq("pl10_");
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        await settings.UpdateSetting(JObject.Parse($$"""
            {"Invoices":[
              {"PaymentHash":"{{withBatch}}","Bolt11":"lnbc1","VerifyUrl":"https://h.example/verify/{{withBatch}}","VerifyHost":"h.example","PayEndpoint":"https://h.example/pay","ExpiresAtUnix":{{exp}},"VerifyBatch":"https://h.example/lnurl/verifyBatch"},
              {"PaymentHash":"{{legacy}}","Bolt11":"lnbc1","VerifyUrl":"https://h.example/verify/{{legacy}}","VerifyHost":"h.example","PayEndpoint":"https://h.example/pay","ExpiresAtUnix":{{exp}}}
            ]}
            """), LNURLVerifyPersistence.SettingName);

        await new LNURLVerifyPersistence(settings).LoadAsync();

        Assert.True(TrackedInvoiceRegistry.TryGet(withBatch, out var a));
        Assert.Equal("https://h.example/lnurl/verifyBatch", a.VerifyBatch);
        Assert.True(TrackedInvoiceRegistry.TryGet(legacy, out var b));
        Assert.Null(b.VerifyBatch);
        TrackedInvoiceRegistry.Remove(withBatch);
        TrackedInvoiceRegistry.Remove(legacy);
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
