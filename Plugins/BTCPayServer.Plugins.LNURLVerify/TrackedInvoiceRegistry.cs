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

    // Recently-settled invoices, kept retrievable as Paid for a grace period. BTCPay's poll path
    // (LightningListener.PollPayment) EVICTS an invoice whose GetInvoice returns null, so a settled
    // invoice must keep reporting Paid until BTCPay has recorded it — not vanish the instant we detect it.
    private static readonly ConcurrentDictionary<string, (LightningInvoice Invoice, DateTimeOffset PruneAfter)> _settled = new();

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
        if (_byHost.TryGetValue(host, out var inner))
            inner.TryRemove(paymentHash, out _);
        // Intentionally do NOT prune the (now-possibly-empty) host bucket: the outer map is bounded by
        // the small set of distinct verify hosts, and pruning it races a concurrent Add for the same
        // host (GetOrAdd could hand back this same inner instance, which we would then delete out from
        // under the just-added invoice — orphaning it).
    }

    /// <summary>
    /// Move a settled invoice into the short-lived settled cache (so GetInvoice keeps returning it as
    /// Paid) and stop tracking it (so the poller no longer polls it). Stores settled BEFORE removing
    /// from tracked so there is never a window where neither map holds the hash.
    /// </summary>
    public static void MarkSettled(string paymentHash, LightningInvoice paid, DateTimeOffset pruneAfter)
    {
        _settled[paymentHash] = (paid, pruneAfter);
        Remove(paymentHash);
    }

    public static bool TryGetSettled(string paymentHash, out LightningInvoice invoice)
    {
        invoice = default!;
        if (!_settled.TryGetValue(paymentHash, out var e)) return false;
        if (e.PruneAfter < DateTimeOffset.UtcNow) { _settled.TryRemove(paymentHash, out _); return false; }
        invoice = e.Invoice;
        return true;
    }

    public static void PruneSettled(DateTimeOffset now)
    {
        foreach (var kv in _settled)
            if (kv.Value.PruneAfter < now)
                _settled.TryRemove(kv.Key, out _);
    }

    public static IReadOnlyCollection<TrackedInvoice> SnapshotByHost(string host) =>
        _byHost.TryGetValue(host, out var inner) ? inner.Values.ToArray() : Array.Empty<TrackedInvoice>();

    public static IReadOnlyCollection<string> Hosts() => _byHost.Keys.ToArray();

    /// <summary>All tracked invoices across every host (used by a connection's ListInvoices, filtered by pay-endpoint).</summary>
    public static IReadOnlyCollection<TrackedInvoice> All() =>
        _byHost.Values.SelectMany(v => v.Values).ToArray();

    public static void PublishSettled(TrackedInvoice t, LightningInvoice inv) => Settled?.Invoke(t, inv);
}
