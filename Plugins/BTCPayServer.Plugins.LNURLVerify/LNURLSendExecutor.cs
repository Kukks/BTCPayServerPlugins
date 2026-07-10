#nullable enable
using System;
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
    private readonly SemaphoreSlim _serialize = new(1, 1);

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

        // Serialize per link: re-fetch a fresh withdraw (fresh k1 + current bounds), then submit, atomically.
        await _serialize.WaitAsync(ct);
        try
        {
            LNURL.LNURLWithdrawRequest w;
            try { w = await RefreshWithdraw(ct); }
            catch (Exception e) { return new PayResponse(PayResult.Error, $"Could not refresh the LNURL-withdraw: {e.Message}"); }

            if (!WithinBounds(amount, w))
                return new PayResponse(PayResult.Error,
                    $"Amount {amount.MilliSatoshi} msat is outside the withdraw bounds " +
                    $"[{w.MinWithdrawable.MilliSatoshi}, {w.MaxWithdrawable.MilliSatoshi}] msat.");

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
                // Transport / non-JSON: we don't know the outcome. Unknown lets BTCPay reconcile later.
                return new PayResponse(PayResult.Unknown, e.Message);
            }

            var status = json["status"]?.Value<string>();
            if (status is null || !status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return new PayResponse(PayResult.Error, json["reason"]?.Value<string>() ?? "LNURL-withdraw was rejected.");

            // Best-effort preimage (non-standard): only trusted if it hashes to the payment hash.
            var paymentHash = bolt11.PaymentHash;
            uint256? preimage = null;
            var preimageStr = json["preimage"]?.Value<string>();
            if (preimageStr is not null && paymentHash is not null &&
                LNURLReceiver.IsValidPreimage(preimageStr, paymentHash.ToString()))
                preimage = new uint256(preimageStr);

            return new PayResponse(PayResult.Ok, new PayDetails
            {
                PaymentHash = paymentHash,
                Status = LightningPaymentStatus.Complete,
                Preimage = preimage
            });
        }
        finally { _serialize.Release(); }
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

    public Task<LightMoney?> GetBalance(CancellationToken ct) =>
        Task.FromResult(_resolved.Withdraw?.CurrentBalance);
}
