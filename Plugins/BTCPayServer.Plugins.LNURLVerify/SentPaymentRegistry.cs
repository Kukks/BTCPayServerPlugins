#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// Static record of outbound (withdraw) sends, keyed by payment hash, so BTCPay's payout reconciliation
/// — which calls GetPayment on a possibly-different client instance — can read the known status instead
/// of getting null.
///
/// NOTE: a bare-bolt11 withdraw send is not verifiable after the fact (the payer never sees the payee's
/// preimage and there is no callback), so a transport-uncertain send is recorded as Pending and CANNOT
/// be auto-resolved to Complete/Failed here — such a payout stays in-progress in BTCPay and may need
/// manual review. Ok/Error sends are recorded as Complete/Failed.
/// </summary>
public static class SentPaymentRegistry
{
    private static readonly ConcurrentDictionary<string, LightningPayment> _payments = new();

    public static void Record(LightningPayment payment)
    {
        if (!string.IsNullOrEmpty(payment.PaymentHash))
            _payments[payment.PaymentHash!] = payment;
    }

    public static bool TryGet(string paymentHash, out LightningPayment payment) =>
        _payments.TryGetValue(paymentHash, out payment!);

    public static IReadOnlyCollection<LightningPayment> All() => _payments.Values.ToArray();
}
