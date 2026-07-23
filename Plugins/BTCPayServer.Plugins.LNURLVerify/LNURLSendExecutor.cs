#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// The send side: pays an arbitrary bolt11 by submitting it to the connection's LNURL-withdraw
/// callback (the linked wallet pays it). Gated by the withdraw's min/max. Sends against one link
/// are serialized (each consumes k1). Preimage is best-effort: read from the callback response if
/// the service returns one (non-standard), validated against the payment hash; otherwise absent.
/// </summary>
public sealed class LNURLSendExecutor
{
    private readonly ResolvedLnurl _resolved;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    // Per-LINK (not per-instance) send lock: BTCPay creates separate client instances for the same
    // connection, so serialization must be shared across them or concurrent Pays would race the
    // k1-refresh+submit. Keyed by the connection identity, same as the send registry.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _linkLocks = new();
    internal static SemaphoreSlim GetLinkLock(string key) => _linkLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    private SemaphoreSlim LinkLock => GetLinkLock(_resolved.PayEndpoint.ToString());

    public LNURLSendExecutor(ResolvedLnurl resolved, HttpClient http, ILogger logger)
    { _resolved = resolved; _http = http; _logger = logger; }

    internal static bool WithinBounds(LightMoney amt, LNURL.LNURLWithdrawRequest w) =>
        amt >= w.MinWithdrawable && amt <= w.MaxWithdrawable;

    public async Task<PayResponse> Pay(string bolt11Str, LightMoney? explicitAmount, CancellationToken ct)
    {
        if (_resolved.Withdraw is null)
            throw new NotSupportedException("This LNURL connection is receive-only and cannot send.");

        BOLT11PaymentRequest bolt11;
        try { bolt11 = BOLT11PaymentRequest.Parse(bolt11Str, LNURLReceiver.InferNetwork(bolt11Str)); }
        catch (Exception e) { return new PayResponse(PayResult.Error, $"Invalid bolt11: {e.Message}"); }

        var amount = explicitAmount ?? bolt11.MinimumAmount;
        if (amount is null || amount == LightMoney.Zero)
            return new PayResponse(PayResult.Error, "Amountless invoices cannot be sent via LNURL-withdraw.");

        // Record the outbound send (scoped to this connection) so BTCPay's payout reconciliation
        // (GetPayment) can read its status.
        void RecordSend(LightningPaymentStatus status, uint256? preimage) =>
            SentPaymentRegistry.Record(_resolved.PayEndpoint.ToString(), new LightningPayment
            {
                Id = bolt11.PaymentHash?.ToString(),
                PaymentHash = bolt11.PaymentHash?.ToString(),
                BOLT11 = bolt11Str,
                Amount = amount,
                AmountSent = status == LightningPaymentStatus.Complete ? amount : null,
                Status = status,
                Preimage = preimage?.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            });

        // Serialize per link (across all client instances of this connection): re-fetch a fresh
        // withdraw (fresh k1 + current bounds), then submit, atomically.
        var link = LinkLock;
        await link.WaitAsync(ct);
        try
        {
            LNURL.LNURLWithdrawRequest w;
            try { w = await RefreshWithdraw(ct); }
            catch (Exception e) { return new PayResponse(PayResult.Error, $"Could not refresh the LNURL-withdraw: {e.Message}"); }

            if (!WithinBounds(amount, w))
                return new PayResponse(PayResult.Error,
                    $"Amount {amount.MilliSatoshi} msat is outside the withdraw bounds " +
                    $"[{w.MinWithdrawable.MilliSatoshi}, {w.MaxWithdrawable.MilliSatoshi}] msat.");
            if (w.CurrentBalance is not null && amount > w.CurrentBalance)
                return new PayResponse(PayResult.Error,
                    $"Amount {amount.MilliSatoshi} msat exceeds the withdraw's current balance " +
                    $"({w.CurrentBalance.MilliSatoshi} msat).");

            var cb = new UriBuilder(w.Callback);
            var q = new StringBuilder(cb.Query.TrimStart('?'));
            if (q.Length > 0) q.Append('&');
            if (!string.IsNullOrEmpty(w.K1)) q.Append("k1=").Append(Uri.EscapeDataString(w.K1)).Append('&');
            q.Append("pr=").Append(Uri.EscapeDataString(bolt11Str));
            cb.Query = q.ToString();

            JObject json;
            try
            {
                using var resp = await _http.GetAsync(cb.Uri, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                json = JObject.Parse(body);
            }
            catch (Exception e)
            {
                // Transport / non-JSON: we don't know the outcome. Pending/Unknown — not auto-resolvable.
                RecordSend(LightningPaymentStatus.Pending, null);
                return new PayResponse(PayResult.Unknown, e.Message);
            }

            var status = json["status"]?.Value<string>();
            if (status is null || !status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                RecordSend(LightningPaymentStatus.Failed, null);
                return new PayResponse(PayResult.Error, json["reason"]?.Value<string>() ?? "LNURL-withdraw was rejected.");
            }

            // Best-effort preimage (non-standard): only trusted if it hashes to the payment hash.
            var paymentHash = bolt11.PaymentHash;
            uint256? preimage = null;
            var preimageStr = json["preimage"]?.Value<string>();
            if (preimageStr is not null && paymentHash is not null &&
                LNURLReceiver.IsValidPreimage(preimageStr, paymentHash.ToString()))
                preimage = new uint256(preimageStr);

            RecordSend(LightningPaymentStatus.Complete, preimage);
            return new PayResponse(PayResult.Ok, new PayDetails
            {
                PaymentHash = paymentHash,
                Status = LightningPaymentStatus.Complete,
                Preimage = preimage
            });
        }
        finally { link.Release(); }
    }

    /// <summary>
    /// Fetches a fresh withdraw request (fresh k1 + current bounds/balance) immediately before a send.
    /// Prefers the withdraw's balanceCheck URL (LUD-14) when present, else re-hits the original withdraw
    /// endpoint. For a reusable link this yields a fresh k1; for a spent single-use voucher the response
    /// will carry an error, surfaced to the caller.
    /// </summary>
    internal async Task<LNURL.LNURLWithdrawRequest> RefreshWithdraw(CancellationToken ct)
    {
        var refreshUrl = _resolved.Withdraw?.BalanceCheck ?? _resolved.WithdrawEndpoint
            ?? throw new InvalidOperationException("No LNURL-withdraw endpoint available to refresh.");
        var json = await LNURLResolver.GetJson(_http, refreshUrl, ct);
        return json.ToObject<LNURL.LNURLWithdrawRequest>()
               ?? throw new FormatException("Could not parse the refreshed LNURL-withdraw response.");
    }

    public async Task<LightMoney?> GetBalance(CancellationToken ct)
    {
        if (_resolved.Withdraw is null) return null;
        // Re-fetch for a live balance (RefreshWithdraw prefers balanceCheck); fall back to the
        // resolve-time snapshot on error rather than reporting a hard failure.
        try { return (await RefreshWithdraw(ct)).CurrentBalance; }
        catch { return _resolved.Withdraw.CurrentBalance; }
    }
}
