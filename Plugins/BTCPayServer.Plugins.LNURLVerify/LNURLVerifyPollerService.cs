#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// The single shared verify poller for every LNURL connection. One loop iterates all tracked
/// invoices grouped by verify-host, polls each host's invoices with bounded concurrency and
/// per-invoice capped backoff, and on settlement publishes to the registry's broadcast (which
/// Listen() subscribers filter to their connection). This collapses the naive
/// one-poll-loop-per-Listen() fan-out into a single host-batched workload.
/// </summary>
public sealed class LNURLVerifyPollerService : IHostedService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int MaxConcurrencyPerCycle = 16;

    private readonly ILogger<LNURLVerifyPollerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    // Concurrent: PollOne runs for many invoices at once under the cycle's concurrency gate.
    private readonly ConcurrentDictionary<string, (int Errors, DateTimeOffset Next)> _backoff = new();
    private Task? _loop;

    /// <summary>Test seam: when set, used instead of a real HTTP verify poll.</summary>
    internal static Func<TrackedInvoice, CancellationToken, Task<LightningInvoice?>>? PollOverride;

    public LNURLVerifyPollerService(ILogger<LNURLVerifyPollerService> logger, IHttpClientFactory httpClientFactory)
        : this(logger, httpClientFactory, DefaultInterval) { }

    internal LNURLVerifyPollerService(ILogger<LNURLVerifyPollerService> logger,
        IHttpClientFactory httpClientFactory, TimeSpan interval)
    { _logger = logger; _httpClientFactory = httpClientFactory; _interval = interval; }

    public Task StartAsync(CancellationToken _)
    {
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
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TrackedInvoiceRegistry.PruneSettled(DateTimeOffset.UtcNow);

                // Poll every tracked invoice across every host under one global concurrency cap, so a
                // slow host interleaves with (rather than blocks) the others.
                var invoices = TrackedInvoiceRegistry.Hosts()
                    .SelectMany(TrackedInvoiceRegistry.SnapshotByHost)
                    .ToArray();
                if (invoices.Length > 0)
                {
                    HttpClient? http = null;
                    if (PollOverride is null)
                    {
                        // One client per cycle, shared across the concurrent polls (HttpClient is
                        // thread-safe for concurrent GETs); bound the timeout (factory default is 100s).
                        http = _httpClientFactory.CreateClient(nameof(LNURLVerifyPollerService));
                        http.Timeout = TimeSpan.FromSeconds(30);
                    }
                    using var gate = new SemaphoreSlim(MaxConcurrencyPerCycle);
                    var tasks = invoices.Select(async t =>
                    {
                        await gate.WaitAsync(ct);
                        try { await PollOne(t, http, ct); }
                        finally { gate.Release(); }
                    });
                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { _logger.LogDebug(e, "LNURL verify poll cycle error"); }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOne(TrackedInvoice t, HttpClient? http, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_backoff.TryGetValue(t.PaymentHash, out var b) && now < b.Next) return;
        if (t.ExpiresAt < now)
        {
            TrackedInvoiceRegistry.Remove(t.PaymentHash);
            _backoff.TryRemove(t.PaymentHash, out _);
            return;
        }

        try
        {
            var inv = PollOverride is not null
                ? await PollOverride(t, ct)
                : await LNURLReceiver.PollAndBuild(t, http!, ct);

            _backoff.TryRemove(t.PaymentHash, out _); // success resets backoff
            if (inv is null) return;

            if (inv.Status == LightningInvoiceStatus.Paid)
            {
                // Keep it retrievable as Paid for a grace window (BTCPay's poll path evicts an invoice
                // whose GetInvoice returns null) and publish for any live listener.
                var pruneAfter = (t.ExpiresAt > DateTimeOffset.UtcNow ? t.ExpiresAt : DateTimeOffset.UtcNow)
                                 + TimeSpan.FromHours(1);
                TrackedInvoiceRegistry.MarkSettled(t.PaymentHash, inv, pruneAfter);
                TrackedInvoiceRegistry.PublishSettled(t, inv);
            }
            else if (inv.Status == LightningInvoiceStatus.Expired)
            {
                TrackedInvoiceRegistry.Remove(t.PaymentHash);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var errors = (_backoff.TryGetValue(t.PaymentHash, out var prev) ? prev.Errors : 0) + 1;
            var delay = Math.Min(_interval.TotalMilliseconds * Math.Pow(2, errors), MaxBackoff.TotalMilliseconds);
            _backoff[t.PaymentHash] = (errors, DateTimeOffset.UtcNow.AddMilliseconds(delay));
            _logger.LogDebug(e, "Error polling LNURL verify for {Hash} (attempt {N})", t.PaymentHash, errors);
        }
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
