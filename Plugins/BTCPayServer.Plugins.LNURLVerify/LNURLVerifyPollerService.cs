#nullable enable
using System;
using System.Collections.Generic;
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
    private readonly Dictionary<string, (int Errors, DateTimeOffset Next)> _backoff = new();
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
                foreach (var host in TrackedInvoiceRegistry.Hosts())
                {
                    var invoices = TrackedInvoiceRegistry.SnapshotByHost(host);
                    if (invoices.Count == 0) continue;
                    using var gate = new SemaphoreSlim(MaxConcurrencyPerCycle);
                    var tasks = invoices.Select(async t =>
                    {
                        await gate.WaitAsync(ct);
                        try { await PollOne(t, ct); }
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

    private async Task PollOne(TrackedInvoice t, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_backoff.TryGetValue(t.PaymentHash, out var b) && now < b.Next) return;
        if (t.ExpiresAt < now)
        {
            TrackedInvoiceRegistry.Remove(t.PaymentHash);
            _backoff.Remove(t.PaymentHash);
            return;
        }

        try
        {
            LightningInvoice? inv;
            if (PollOverride is not null)
            {
                inv = await PollOverride(t, ct);
            }
            else
            {
                var http = _httpClientFactory.CreateClient(nameof(LNURLVerifyPollerService));
                inv = await LNURLReceiver.PollAndBuild(t, http, ct);
            }

            _backoff.Remove(t.PaymentHash); // success resets backoff
            if (inv is null) return;

            if (inv.Status == LightningInvoiceStatus.Paid)
            {
                TrackedInvoiceRegistry.Remove(t.PaymentHash);
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
