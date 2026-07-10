#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// The receive side: creates invoices via the LNURL-pay callback (capturing the LUD-21 verify URL)
/// and reads settlement via that verify URL. The verify-poll-and-build logic is static so the shared
/// poller can drive it without any per-connection state — a tracked invoice carries everything needed.
/// </summary>
public sealed class LNURLReceiver
{
    private readonly ResolvedLnurl _resolved;
    private readonly Network _network;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public LNURLReceiver(ResolvedLnurl resolved, Network network, HttpClient http, ILogger logger)
    { _resolved = resolved; _network = network; _http = http; _logger = logger; }

    private const string VerifyUnsupportedMessage =
        "This LNURL-pay endpoint does not support the LUD-21 'verify' extension, which is required to detect " +
        "payment settlement. Use a verify-capable LNURL server (e.g. BTCPay Server or blink-lnurl-server).";

    /// <summary>
    /// Config-time probe: requests a minimal invoice from the pay callback and checks that the LUD-21
    /// verify field is present. Returns null when verify is supported, or a user-facing error message.
    /// (LUD-21 exposes verify only in the callback response, so this can't be checked from metadata alone.)
    /// </summary>
    public async Task<string?> CheckVerifySupport(CancellationToken ct)
    {
        JObject meta;
        try { meta = await LNURLResolver.GetJson(_http, _resolved.PayEndpoint, ct); }
        catch (Exception e) { return e.Message; }

        var callback = meta["callback"]?.Value<string>();
        if (string.IsNullOrEmpty(callback)) return "The LNURL-pay endpoint is missing a callback URL.";
        var min = meta["minSendable"]?.Value<long>() ?? 1000;

        var cb = new UriBuilder(callback);
        var q = new StringBuilder(cb.Query.TrimStart('?'));
        if (q.Length > 0) q.Append('&');
        q.Append("amount=").Append(min);
        cb.Query = q.ToString();

        JObject json;
        try { json = await LNURLResolver.GetJson(_http, cb.Uri, ct); }
        catch (Exception e) { return $"Could not request a probe invoice: {e.Message}"; }

        var verify = json["verify"]?.Value<string>();
        if (string.IsNullOrEmpty(verify) || !Uri.TryCreate(verify, UriKind.Absolute, out _))
            return VerifyUnsupportedMessage;
        return null;
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney? amount, string? description,
        CreateInvoiceParams? p, CancellationToken ct)
    {
        if (amount is null)
            throw new NotSupportedException(
                "LNURL requires an invoice amount; amountless/top-up invoices are not supported.");

        var meta = await LNURLResolver.GetJson(_http, _resolved.PayEndpoint, ct);
        var callback = meta["callback"]?.Value<string>()
                       ?? throw new Exception("LNURL-pay response is missing a callback URL.");
        var min = meta["minSendable"]?.Value<long>() ?? 1;
        var max = meta["maxSendable"]?.Value<long>() ?? long.MaxValue;
        var msat = amount.MilliSatoshi;
        if (msat < min) throw new Exception($"Amount {msat} msat is below the minimum ({min} msat).");
        if (msat > max) throw new Exception($"Amount {msat} msat is above the maximum ({max} msat).");

        var cb = new UriBuilder(callback);
        var q = new StringBuilder(cb.Query.TrimStart('?'));
        if (q.Length > 0) q.Append('&');
        q.Append("amount=").Append(msat);
        var commentAllowed = meta["commentAllowed"]?.Value<int>() ?? 0;
        if (commentAllowed > 0 && !string.IsNullOrEmpty(description))
        {
            var c = description!.Length > commentAllowed ? description.Substring(0, commentAllowed) : description;
            q.Append("&comment=").Append(Uri.EscapeDataString(c));
        }
        cb.Query = q.ToString();

        var json = await LNURLResolver.GetJson(_http, cb.Uri, ct);
        var pr = json["pr"]?.Value<string>() ?? throw new Exception("LNURL callback did not return an invoice.");
        var bolt11 = BOLT11PaymentRequest.Parse(pr, _network);

        // Security guards against a malicious/broken LNURL server.
        if (bolt11.MinimumAmount != LightMoney.MilliSatoshis(msat))
            throw new Exception(
                $"LNURL returned an invoice for {bolt11.MinimumAmount.MilliSatoshi} msat but {msat} was requested.");
        if (p?.DescriptionHash is { } dh && dh != bolt11.DescriptionHash)
            throw new Exception("LNURL returned an invoice with a mismatched or missing description hash.");

        var paymentHash = bolt11.PaymentHash?.ToString() ?? throw new Exception("Invoice has no payment hash.");
        // LUD-21: the verify URL is returned by the callback. Without it we cannot detect settlement,
        // so fail loudly rather than track a guessed URL that would silently never confirm payment.
        var verifyUrl = json["verify"]?.Value<string>();
        if (string.IsNullOrEmpty(verifyUrl) || !Uri.TryCreate(verifyUrl, UriKind.Absolute, out var verifyUri))
            throw new NotSupportedException(VerifyUnsupportedMessage);
        var verifyHost = verifyUri.Host;

        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            paymentHash, pr, verifyUrl, verifyHost, _resolved.PayEndpoint.ToString(), bolt11.ExpiryDate));

        return new LightningInvoice
        {
            Id = paymentHash,
            PaymentHash = paymentHash,
            BOLT11 = pr,
            Amount = bolt11.MinimumAmount,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = bolt11.ExpiryDate
        };
    }

    public Task<LightningInvoice?> GetInvoice(string paymentHash, CancellationToken ct)
        => TrackedInvoiceRegistry.TryGet(paymentHash, out var t)
            ? PollAndBuild(t, _http, ct)
            : Task.FromResult<LightningInvoice?>(null);

    /// <summary>
    /// Polls a tracked invoice's LUD-21 verify URL and builds its status. Connection-agnostic:
    /// used both by GetInvoice and by the shared poller. Returns a minimal Unpaid invoice (never
    /// null) on a transient transport error so BTCPay/the poller keeps the invoice tracked.
    /// </summary>
    public static async Task<LightningInvoice?> PollAndBuild(TrackedInvoice t, HttpClient http, CancellationToken ct)
    {
        JObject? json = null;
        bool transportError = false;
        try
        {
            using var resp = await http.GetAsync(t.VerifyUrl, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) json = JObject.Parse(body);
            else transportError = true;
        }
        catch { transportError = true; }

        if (json?["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
            return null; // genuine not-found

        if (json is null)
        {
            if (transportError)
                return new LightningInvoice
                { Id = t.PaymentHash, PaymentHash = t.PaymentHash, Status = LightningInvoiceStatus.Unpaid };
            return null;
        }

        var settled = json["settled"]?.Value<bool>() ?? false;
        var preimage = json["preimage"]?.Value<string>();
        return BuildInvoice(t, settled, preimage);
    }

    private static LightningInvoice BuildInvoice(TrackedInvoice t, bool settled, string? preimage)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(t.Bolt11, InferNetwork(t.Bolt11));
        var status = settled ? LightningInvoiceStatus.Paid
            : t.ExpiresAt < DateTimeOffset.UtcNow ? LightningInvoiceStatus.Expired
            : LightningInvoiceStatus.Unpaid;
        string? valid = settled && preimage is not null && IsValidPreimage(preimage, t.PaymentHash) ? preimage : null;
        return new LightningInvoice
        {
            Id = t.PaymentHash,
            PaymentHash = t.PaymentHash,
            BOLT11 = t.Bolt11,
            Amount = bolt11.MinimumAmount,
            AmountReceived = settled ? bolt11.MinimumAmount : null,
            Status = status,
            Preimage = valid,
            PaidAt = settled ? DateTimeOffset.UtcNow : null,
            ExpiresAt = t.ExpiresAt
        };
    }

    public static bool IsValidPreimage(string? preimage, string paymentHash)
    {
        preimage = preimage?.Trim() ?? "";
        paymentHash = paymentHash.Trim().ToLowerInvariant();
        if (preimage.Length != 64 || paymentHash.Length != 64) return false;
        foreach (var c in preimage) if (!Uri.IsHexDigit(c)) return false;
        try
        {
            var bytes = Encoders.Hex.DecodeData(preimage);
            var computed = Encoders.Hex.EncodeData(Hashes.SHA256(bytes));
            return computed.Equals(paymentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static Network InferNetwork(string bolt11) =>
        bolt11.StartsWith("lnbcrt", StringComparison.OrdinalIgnoreCase) ? Network.RegTest
        : bolt11.StartsWith("lntb", StringComparison.OrdinalIgnoreCase) ? Network.TestNet
        : Network.Main;
}
