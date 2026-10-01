#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// The single shared verify poller for every LNURL connection. Each cycle checks every tracked invoice:
/// those whose service advertised a LUD-XX verifyBatch endpoint with one request per endpoint (chunked),
/// the rest with one LUD-21 verify GET each under a global concurrency cap. Settlements are published to
/// the registry's broadcast, which Listen() subscribers filter to their connection.
/// </summary>
public sealed class LNURLVerifyPollerService : IHostedService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int MaxConcurrencyPerCycle = 16;
    private static readonly TimeSpan PersistThrottle = TimeSpan.FromSeconds(10);
    // lnurl-server answers 414 above 250 verify URLs per one-shot request.
    internal const int MaxBatchSize = 250;
    private static readonly TimeSpan UnsupportedFor = TimeSpan.FromHours(1);

    private readonly ILogger<LNURLVerifyPollerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _interval;
    private readonly LNURLVerifyPersistence? _persistence;
    private readonly CancellationTokenSource _cts = new();
    // Concurrent: PollOne runs for many invoices at once under the cycle's concurrency gate.
    private readonly ConcurrentDictionary<string, (int Errors, DateTimeOffset Next)> _backoff = new();
    private readonly ConcurrentDictionary<string, (int Errors, DateTimeOffset Next)> _batchBackoff = new();
    private readonly ConcurrentDictionary<string, int> _chunkSize = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unsupportedUntil = new();
    private Task? _loop;
    private int _lastPersistedVersion;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    /// <summary>Test seam: when set, every invoice is polled through it (no HTTP, no batching).</summary>
    internal static Func<TrackedInvoice, CancellationToken, Task<LightningInvoice?>>? PollOverride;

    public LNURLVerifyPollerService(ILogger<LNURLVerifyPollerService> logger, IHttpClientFactory httpClientFactory,
        ISettingsRepository settings)
        : this(logger, httpClientFactory, DefaultInterval, new LNURLVerifyPersistence(settings)) { }

    internal LNURLVerifyPollerService(ILogger<LNURLVerifyPollerService> logger,
        IHttpClientFactory httpClientFactory, TimeSpan interval, LNURLVerifyPersistence? persistence = null)
    { _logger = logger; _httpClientFactory = httpClientFactory; _interval = interval; _persistence = persistence; }

    public Task StartAsync(CancellationToken _)
    {
        // Persisted invoices are re-seeded by the connection handler on first Create (which BTCPay calls
        // before any GetInvoice), guaranteeing re-arm before the core startup poll can null-evict them.
        // The poller only handles the SAVE side.
        _lastPersistedVersion = TrackedInvoiceRegistry.Version;
        _loop = Task.Run(() => Loop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* cancellation */ }
        }
        // Final flush on graceful shutdown so a planned restart loses nothing.
        if (_persistence != null)
        {
            try { await _persistence.SaveAsync(); } catch (Exception e) { _logger.LogDebug(e, "Final LNURL persist failed"); }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TrackedInvoiceRegistry.PruneSettled(DateTimeOffset.UtcNow);
                SentPaymentRegistry.Prune(DateTimeOffset.UtcNow.AddHours(-24)); // keep the registry bounded

                var now = DateTimeOffset.UtcNow;
                var invoices = TrackedInvoiceRegistry.All().ToArray();
                foreach (var t in invoices.Where(t => t.ExpiresAt < now))
                    TrackedInvoiceRegistry.Remove(t.PaymentHash);
                var live = invoices.Where(t => t.ExpiresAt >= now).ToArray();
                TrackedInvoiceRegistry.PruneResults();

                // Drop backoff entries for invoices no longer tracked (e.g. cancelled between polls, which
                // the poller never observes otherwise) so _backoff can't grow over the process lifetime.
                if (!_backoff.IsEmpty)
                {
                    var tracked = new HashSet<string>(live.Select(i => i.PaymentHash));
                    foreach (var key in _backoff.Keys)
                        if (!tracked.Contains(key))
                            _backoff.TryRemove(key, out _);
                }

                if (live.Length > 0)
                {
                    HttpClient? http = null;
                    if (PollOverride is null)
                    {
                        // One client per cycle, shared across the concurrent polls (HttpClient is
                        // thread-safe for concurrent GETs); bound the timeout (factory default is 100s).
                        http = _httpClientFactory.CreateClient(nameof(LNURLVerifyPollerService));
                        http.Timeout = TimeSpan.FromSeconds(30);
                    }
                    var batches = http is null
                        ? Array.Empty<IGrouping<string, TrackedInvoice>>()
                        : live.Where(t => Batchable(t, now)).GroupBy(t => t.VerifyBatch!).ToArray();
                    using var gate = new SemaphoreSlim(MaxConcurrencyPerCycle);
                    var singles = live.Where(t => http is null || !Batchable(t, now)).Select(async t =>
                    {
                        await gate.WaitAsync(ct);
                        try { await PollOne(t, http, ct); }
                        finally { gate.Release(); }
                    });
                    await Task.WhenAll(singles.Concat(batches.Select(g => PollBatch(g.Key, g.ToArray(), http!, ct))));
                }

                // Persist the tracked set (throttled, only when it changed) so it survives a restart.
                if (_persistence != null)
                {
                    var version = TrackedInvoiceRegistry.Version;
                    var persistAt = DateTimeOffset.UtcNow;
                    if (version != _lastPersistedVersion && persistAt - _lastPersist > PersistThrottle)
                    {
                        try { await _persistence.SaveAsync(); _lastPersistedVersion = version; _lastPersist = persistAt; }
                        catch (Exception e) { _logger.LogDebug(e, "Failed to persist LNURL tracked invoices"); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { _logger.LogDebug(e, "LNURL verify poll cycle error"); }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool Batchable(TrackedInvoice t, DateTimeOffset now) =>
        t.VerifyBatch is { } endpoint && !(_unsupportedUntil.TryGetValue(endpoint, out var until) && now < until);

    private async Task PollOne(TrackedInvoice t, HttpClient? http, CancellationToken ct)
    {
        if (_backoff.TryGetValue(t.PaymentHash, out var b) && DateTimeOffset.UtcNow < b.Next) return;
        try
        {
            var inv = PollOverride is not null
                ? await PollOverride(t, ct)
                : await LNURLReceiver.PollAndBuild(t, http!, ct);
            _backoff.TryRemove(t.PaymentHash, out _); // success resets backoff
            Apply(t, inv);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var next = NextBackoff(_backoff, t.PaymentHash);
            _backoff[t.PaymentHash] = next;
            _logger.LogDebug(e, "Error polling LNURL verify for {Hash} (attempt {N})", t.PaymentHash, next.Errors);
        }
    }

    private async Task PollBatch(string endpoint, TrackedInvoice[] items, HttpClient http, CancellationToken ct)
    {
        if (_batchBackoff.TryGetValue(endpoint, out var b) && DateTimeOffset.UtcNow < b.Next) return;
        var size = _chunkSize.TryGetValue(endpoint, out var remembered) ? remembered : MaxBatchSize;
        for (var i = 0; i < items.Length;)
        {
            var chunk = items.Skip(i).Take(size).ToArray();
            var outcome = await VerifyBatchClient.Fetch(http, endpoint, chunk.Select(t => t.VerifyUrl).ToArray(), ct);
            switch (outcome.Kind)
            {
                case BatchOutcomeKind.TooLong when chunk.Length > 1:
                    size = _chunkSize[endpoint] = Math.Max(1, chunk.Length / 2);
                    continue;
                case BatchOutcomeKind.Unsupported:
                    _unsupportedUntil[endpoint] = DateTimeOffset.UtcNow + UnsupportedFor;
                    _logger.LogDebug("verifyBatch {Endpoint} is unusable; polling its invoices one by one for {Duration}",
                        endpoint, UnsupportedFor);
                    return;
                case BatchOutcomeKind.Ok:
                    _batchBackoff.TryRemove(endpoint, out _);
                    foreach (var t in chunk)
                    {
                        if (!outcome.Results!.TryGetValue(t.VerifyUrl, out var item)) continue;
                        try { Apply(t, LNURLReceiver.FromVerifyJson(t, item)); }
                        catch (Exception e) { _logger.LogDebug(e, "Unreadable verifyBatch item for {Hash}", t.PaymentHash); }
                    }
                    i += chunk.Length;
                    break;
                default:
                    _batchBackoff[endpoint] = NextBackoff(_batchBackoff, endpoint);
                    _logger.LogDebug("verifyBatch {Endpoint} failed ({Kind}: {Error})", endpoint, outcome.Kind, outcome.Error);
                    return;
            }
        }
    }

    private (int Errors, DateTimeOffset Next) NextBackoff(
        ConcurrentDictionary<string, (int Errors, DateTimeOffset Next)> backoff, string key)
    {
        var errors = (backoff.TryGetValue(key, out var prev) ? prev.Errors : 0) + 1;
        var delay = Math.Min(_interval.TotalMilliseconds * Math.Pow(2, errors), MaxBackoff.TotalMilliseconds);
        return (errors, DateTimeOffset.UtcNow.AddMilliseconds(delay));
    }

    private static void Apply(TrackedInvoice t, LightningInvoice? inv)
    {
        if (inv?.Status == LightningInvoiceStatus.Paid)
        {
            // Keep it retrievable as Paid for a grace window (BTCPay's poll path evicts an invoice whose
            // GetInvoice returns null) and publish for any live listener.
            var pruneAfter = (t.ExpiresAt > DateTimeOffset.UtcNow ? t.ExpiresAt : DateTimeOffset.UtcNow)
                             + TimeSpan.FromHours(1);
            TrackedInvoiceRegistry.MarkSettled(t.PaymentHash, inv, pruneAfter);
            TrackedInvoiceRegistry.PublishSettled(t, inv);
        }
        else if (inv?.Status == LightningInvoiceStatus.Expired)
            TrackedInvoiceRegistry.Remove(t.PaymentHash);
        else
            TrackedInvoiceRegistry.RecordResult(t.PaymentHash, inv);
    }
}

/// <summary>
/// A cheap per-connection subscriber over the registry's settled broadcast, filtered to the
/// connection's invoices. No poll loop of its own — the shared poller does the work.
/// </summary>
public sealed class LNURLVerifyListener : ILightningInvoiceListener
{
    private readonly Channel<LightningInvoice> _channel = Channel.CreateUnbounded<LightningInvoice>();
    private readonly Action<TrackedInvoice, LightningInvoice> _handler;

    public LNURLVerifyListener(Func<TrackedInvoice, bool> isMine)
    {
        _handler = (t, inv) => { if (isMine(t)) _channel.Writer.TryWrite(inv); };
        TrackedInvoiceRegistry.Settled += _handler;
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken ct) => await _channel.Reader.ReadAsync(ct);

    public void Dispose()
    {
        TrackedInvoiceRegistry.Settled -= _handler;
        _channel.Writer.TryComplete();
    }
}
