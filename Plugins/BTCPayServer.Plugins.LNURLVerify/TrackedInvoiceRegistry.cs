#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="PayEndpoint">The connection's LNURL-pay endpoint (its identity for Listen() filtering).</param>
/// <param name="VerifyHost">The host of the verify URL — the registry's grouping key for batched polling.</param>
public sealed record TrackedInvoice(
    string PaymentHash,
    string Bolt11,
    string VerifyUrl,
    string VerifyHost,
    string PayEndpoint,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Static, verify-host-keyed registry shared across every client instance BTCPay creates for a
/// connection, plus a broadcast of settlements for Listen() subscribers. Static because BTCPay
/// constructs a separate ILightningClient for its payment listener than the one that created the
/// invoice — per-instance state would be invisible to the listener/poller.
/// </summary>
public static class TrackedInvoiceRegistry
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackedInvoice>> _byHost = new();
    private static readonly ConcurrentDictionary<string, string> _hostOf = new();

    /// <summary>Fired when the poller observes a settled invoice. Listeners filter to their connection.</summary>
    public static event Action<TrackedInvoice, LightningInvoice>? Settled;

    public static void Add(TrackedInvoice t)
    {
        _byHost.GetOrAdd(t.VerifyHost, _ => new()).AddOrUpdate(t.PaymentHash, t, (_, __) => t);
        _hostOf[t.PaymentHash] = t.VerifyHost;
    }

    public static bool TryGet(string paymentHash, out TrackedInvoice t)
    {
        t = default!;
        return _hostOf.TryGetValue(paymentHash, out var host)
               && _byHost.TryGetValue(host, out var inner)
               && inner.TryGetValue(paymentHash, out t!);
    }

    public static void Remove(string paymentHash)
    {
        if (!_hostOf.TryRemove(paymentHash, out var host)) return;
        if (!_byHost.TryGetValue(host, out var inner)) return;
        inner.TryRemove(paymentHash, out _);
        if (inner.IsEmpty)
            _byHost.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, TrackedInvoice>>(host, inner));
    }

    public static IReadOnlyCollection<TrackedInvoice> SnapshotByHost(string host) =>
        _byHost.TryGetValue(host, out var inner) ? inner.Values.ToArray() : Array.Empty<TrackedInvoice>();

    public static IReadOnlyCollection<string> Hosts() => _byHost.Keys.ToArray();

    public static void PublishSettled(TrackedInvoice t, LightningInvoice inv) => Settled?.Invoke(t, inv);
}
