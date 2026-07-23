#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// Static record of outbound (withdraw) sends, keyed by payment hash and tagged with the owning
/// connection, so BTCPay's payout reconciliation (GetPayment on a possibly-different client instance)
/// can read the known status — while one store cannot read another store's sends via the per-store
/// Greenfield ListPayments API.
///
/// NOTE: a bare-bolt11 withdraw send is not verifiable after the fact (the payer never sees the payee's
/// preimage and there is no callback), so a transport-uncertain send is recorded as Pending and CANNOT
/// be auto-resolved — such a payout stays in-progress in BTCPay and may need manual review. Ok/Error
/// sends are recorded as Complete/Failed.
/// </summary>
public static class SentPaymentRegistry
{
    private static readonly ConcurrentDictionary<string, (LightningPayment Payment, string Connection)> _payments = new();

    public static void Record(string connection, LightningPayment payment)
    {
        if (!string.IsNullOrEmpty(payment.PaymentHash))
            _payments[payment.PaymentHash!] = (payment, connection);
    }

    /// <summary>Look up a send by hash, scoped to the owning connection (defends against cross-store reads).</summary>
    public static bool TryGet(string connection, string paymentHash, out LightningPayment payment)
    {
        if (_payments.TryGetValue(paymentHash, out var e) && e.Connection == connection)
        {
            payment = e.Payment;
            return true;
        }
        payment = default!;
        return false;
    }

    public static IReadOnlyCollection<LightningPayment> ListByConnection(string connection) =>
        _payments.Values.Where(e => e.Connection == connection).Select(e => e.Payment).ToArray();

    /// <summary>
    /// Drop terminal (Complete/Failed) send records older than <paramref name="cutoff"/> so this static
    /// registry stays bounded under mass payouts. Pending (transport-uncertain) records are deliberately
    /// KEPT — dropping one would make GetPayment return null, which BTCPay resolves to Cancelled, so a
    /// send that may actually have gone through could be re-paid. Uncertain sends stay for manual review.
    /// </summary>
    public static void Prune(DateTimeOffset cutoff)
    {
        foreach (var kv in _payments)
            if (kv.Value.Payment.Status != LightningPaymentStatus.Pending && kv.Value.Payment.CreatedAt < cutoff)
                _payments.TryRemove(kv.Key, out _);
    }
}
