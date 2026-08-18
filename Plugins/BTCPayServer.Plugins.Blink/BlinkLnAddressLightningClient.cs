#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Blink;

/// <summary>
/// A receive-only Lightning client for Blink non-custodial (Spark) accounts.
///
/// Non-custodial Blink accounts have no API key and no GraphQL wallet-id. The only
/// server-brokered receive path is LNURL-pay (LUD-06/LUD-16) served by blink-lnurl-server
/// at https://{domain}/.well-known/lnurlp/{username}. Payment settlement is detected via the
/// LUD-21 "verify" URL (returns { settled, preimage, pr }).
///
/// This client therefore only supports receiving. Sending, balance and channel operations
/// require the wallet seed and are intentionally not supported.
/// </summary>
public class BlinkLnAddressLightningClient : IExtendedLightningClient
{
    // "user@domain". For USD (USDB, not yet live server-side) the LNURL username carries a
    // "+usd" modifier, e.g. "user+usd".
    private readonly string _lightningAddress;
    private readonly string _username;
    private readonly string _domain;
    private readonly bool _usd;
    private readonly Network _network;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    private record TrackedInvoice(string Bolt11, string? VerifyUrl, DateTimeOffset ExpiresAt)
    {
        // When the invoice was first tracked locally. Drives the age-stepped poll interval (F3):
        // fresh invoices poll fast for quick settlement feedback, long-lived unpaid ones back off.
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    // BTCPay creates SEPARATE client instances for creating invoices and for its payment
    // listener/poller. In-memory per-instance state would therefore not be visible to the listener.
    // We share tracked invoices across all instances of the same lightning address via a static,
    // address-keyed registry so the listener can poll invoices created by another instance.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackedInvoice>>
        _sharedTracked = new();

    private ConcurrentDictionary<string, TrackedInvoice> _tracked =>
        _sharedTracked.GetOrAdd(_lightningAddress, _ => new ConcurrentDictionary<string, TrackedInvoice>());

    /// <summary>
    /// Removes a tracked invoice and prunes the outer address entry once it is empty, so the static
    /// registry stays bounded by the number of currently-open invoices rather than growing for every
    /// lightning address ever configured.
    /// </summary>
    private void RemoveTrackedInvoice(string paymentHash)
    {
        if (!_sharedTracked.TryGetValue(_lightningAddress, out var inner))
            return;
        inner.TryRemove(paymentHash, out _);
        if (inner.IsEmpty)
        {
            // Only remove the outer key if it is still the (now-empty) instance we just pruned, to
            // avoid dropping a dictionary that another thread has concurrently repopulated.
            _sharedTracked.TryRemove(
                new System.Collections.Generic.KeyValuePair<string, ConcurrentDictionary<string, TrackedInvoice>>(
                    _lightningAddress, inner));
        }
    }

    // The origin (scheme://host[:port]) of the LNURL-pay callback, which is also the host that
    // serves the LUD-21 verify endpoint (e.g. https://lnurl.blink.sv). It is NOT the lightning
    // address domain (blink.sv). Cached so a stateless GetInvoice can rebuild the verify URL.
    private string? _verifyOrigin;

    public BlinkLnAddressLightningClient(string lightningAddress, bool usd, Network network,
        HttpClient httpClient, ILogger logger)
    {
        _lightningAddress = lightningAddress;
        (_username, _domain) = ParseLightningAddress(lightningAddress);
        _usd = usd;
        _network = network;
        _httpClient = httpClient;
        _logger = logger;

        // Identify plugin verify/LNURL traffic so operators can attribute it in server telemetry
        // (and so the path-scoped rate limit can be tuned per client type). Multiple client
        // instances may share one HttpClient, so only set the UA if not already present.
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>Product-info User-Agent header value identifying this plugin (name/version).</summary>
    internal static string UserAgent =>
        $"BTCPayServer.Plugins.Blink/{typeof(BlinkLnAddressLightningClient).Assembly.GetName().Version}";

    /// <summary>Splits a "user@domain" lightning address into its parts, throwing on an invalid address.</summary>
    internal static (string Username, string Domain) ParseLightningAddress(string lightningAddress)
    {
        var parts = (lightningAddress ?? "").Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new FormatException($"Invalid Blink lightning address '{lightningAddress}'");
        return (parts[0], parts[1]);
    }

    /// <summary>Builds the LNURL-pay metadata URL for a Blink lightning address. USDB uses a "+usd"
    /// wallet modifier on the local part; localhost domains use http, everything else https.</summary>
    internal static Uri BuildLnurlpMetadataUri(string username, string domain, bool usd)
    {
        var localPart = usd ? $"{username}+usd" : username;
        var scheme = domain.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                     domain.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            ? "http"
            : "https";
        return new Uri($"{scheme}://{domain}/.well-known/lnurlp/{Uri.EscapeDataString(localPart)}");
    }

    private Uri LnurlpMetadataUri => BuildLnurlpMetadataUri(_username, _domain, _usd);

    private async Task<JObject> FetchLnurlMetadata(CancellationToken cancellation)
    {
        using var resp = await _httpClient.GetAsync(LnurlpMetadataUri, cancellation);
        var body = await resp.Content.ReadAsStringAsync(cancellation);
        if (!resp.IsSuccessStatusCode)
            throw new Exception(
                $"Blink lightning address '{_lightningAddress}' not found or unavailable (HTTP {(int)resp.StatusCode}).");
        var json = JObject.Parse(body);
        if (json["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
            throw new Exception(json["reason"]?.Value<string>() ??
                                $"Blink lightning address '{_lightningAddress}' returned an error.");
        if (json["tag"]?.Value<string>() != "payRequest")
            throw new Exception($"'{_lightningAddress}' is not a valid LNURL-pay endpoint.");

        // Cache the callback origin; the LUD-21 verify endpoint lives on the same host.
        if (ExtractOrigin(json["callback"]?.Value<string>()) is { } origin)
            _verifyOrigin = origin;

        return json;
    }

    /// <summary>
    /// Returns the origin (scheme://host[:port]) that serves the LUD-21 verify endpoint.
    /// Cached across calls; derived from the LNURL-pay callback URL when not yet known.
    /// This is required because a fresh client instance (e.g. the one BTCPay creates for its
    /// payment listener/poller) has no in-memory state and must reconstruct the verify URL.
    /// </summary>
    private async Task<string> GetVerifyOrigin(CancellationToken cancellation)
    {
        if (_verifyOrigin is not null)
            return _verifyOrigin;
        await FetchLnurlMetadata(cancellation);
        return _verifyOrigin ?? throw new Exception("Could not determine the Blink LNURL verify endpoint.");
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = new())
    {
        return await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = new())
    {
        var metadata = await FetchLnurlMetadata(cancellation);
        var callback = metadata["callback"]?.Value<string>();
        if (string.IsNullOrEmpty(callback))
            throw new Exception("LNURL-pay response is missing a callback URL.");

        // BTCPay allows a null amount for top-up/amountless invoices, but LNURL-pay is inherently
        // amount-driven, so we require a concrete amount and fail with a clear message otherwise.
        if (createInvoiceRequest.Amount is null)
            throw new NotSupportedException(
                "Blink non-custodial (Spark) accounts require an invoice amount; amountless/top-up invoices are not supported.");

        var amountMsat = createInvoiceRequest.Amount.MilliSatoshi;
        var min = metadata["minSendable"]?.Value<long>() ?? 1;
        var max = metadata["maxSendable"]?.Value<long>() ?? long.MaxValue;
        ValidateAmountBounds(amountMsat, min, max);

        var callbackUri = new UriBuilder(callback);
        var query = new StringBuilder(callbackUri.Query.TrimStart('?'));
        if (query.Length > 0) query.Append('&');
        query.Append("amount=").Append(amountMsat);
        // Pass along a description/comment when the endpoint allows comments (LUD-12).
        var commentAllowed = metadata["commentAllowed"]?.Value<int>() ?? 0;
        if (commentAllowed > 0 && !string.IsNullOrEmpty(createInvoiceRequest.Description))
        {
            var comment = createInvoiceRequest.Description!;
            if (comment.Length > commentAllowed) comment = comment.Substring(0, commentAllowed);
            query.Append("&comment=").Append(Uri.EscapeDataString(comment));
        }
        callbackUri.Query = query.ToString();

        using var resp = await _httpClient.GetAsync(callbackUri.Uri, cancellation);
        var body = await resp.Content.ReadAsStringAsync(cancellation);
        // Guard the HTTP status before parsing: a non-2xx response may carry a non-JSON body
        // (e.g. an HTML 500 from the LNURL server), which would otherwise throw an opaque parse error.
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Blink LNURL callback failed (HTTP {(int)resp.StatusCode}).");
        var json = JObject.Parse(body);
        if (json["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
            throw new Exception(json["reason"]?.Value<string>() ?? "Blink LNURL callback returned an error.");

        var pr = json["pr"]?.Value<string>();
        if (string.IsNullOrEmpty(pr))
            throw new Exception("Blink LNURL callback did not return an invoice.");

        var bolt11 = BOLT11PaymentRequest.Parse(pr, _network);

        // Verify the returned invoice matches what we asked for (guards against a malicious server).
        if (bolt11.MinimumAmount != LightMoney.MilliSatoshis(amountMsat))
            throw new Exception(
                $"Blink returned an invoice for {bolt11.MinimumAmount.MilliSatoshi} msat but {amountMsat} msat was requested.");
        // Strict check: if a description hash was requested, the returned invoice must carry the
        // exact same hash. A missing/stripped `h` tag (bolt11.DescriptionHash == null) is also a
        // mismatch, so a compromised LNURL server cannot bypass the check by dropping the tag.
        if (createInvoiceRequest.DescriptionHash is { } dh && dh != bolt11.DescriptionHash)
            throw new Exception("Blink returned an invoice with a mismatched or missing description hash.");

        var paymentHash = bolt11.PaymentHash?.ToString() ?? throw new Exception("Invoice has no payment hash.");
        var verifyUrl = json["verify"]?.Value<string>();
        if (ExtractOrigin(verifyUrl) is { } vOrigin)
            _verifyOrigin = vOrigin;
        verifyUrl ??= BuildVerifyUrl(paymentHash);
        var expiresAt = bolt11.ExpiryDate;

        _tracked[paymentHash] = new TrackedInvoice(pr, verifyUrl, expiresAt);

        return new LightningInvoice
        {
            Id = paymentHash,
            PaymentHash = paymentHash,
            BOLT11 = pr,
            Amount = bolt11.MinimumAmount,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = expiresAt
        };
    }

    // LUD-21 verify URLs on blink-lnurl-server are {verifyOrigin}/verify/{paymentHash}, where
    // verifyOrigin is the host of the LNURL-pay callback (e.g. https://lnurl.blink.sv) - NOT the
    // lightning address domain. Requires the origin to have already been resolved.
    private string BuildVerifyUrl(string paymentHash)
    {
        if (_verifyOrigin is null)
            throw new InvalidOperationException("Verify origin not resolved yet.");
        return BuildVerifyUrl(_verifyOrigin, paymentHash);
    }

    /// <summary>Builds the LUD-21 verify URL from the verify origin (the LNURL-pay callback host)
    /// and the payment hash.</summary>
    internal static string BuildVerifyUrl(string verifyOrigin, string paymentHash)
        => $"{verifyOrigin}/verify/{paymentHash}";

    /// <summary>Extracts the origin (scheme://host[:port]) from an absolute http/https URL, or null if
    /// it is not a valid absolute http/https URL. Used to derive the verify-endpoint host from the
    /// LNURL callback URL. Non-http(s) schemes (e.g. the file:// that some platforms infer from a bare
    /// relative path) are rejected.</summary>
    internal static string? ExtractOrigin(string? url)
        => !string.IsNullOrEmpty(url)
           && Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.GetLeftPart(UriPartial.Authority)
            : null;

    /// <summary>Throws if the requested amount (msat) is outside the LNURL min/max sendable bounds.</summary>
    internal static void ValidateAmountBounds(long amountMsat, long minMsat, long maxMsat)
    {
        if (amountMsat < minMsat)
            throw new Exception(
                $"Amount {amountMsat} msat is below the minimum accepted by this Blink address ({minMsat} msat).");
        if (amountMsat > maxMsat)
            throw new Exception(
                $"Amount {amountMsat} msat is above the maximum accepted by this Blink address ({maxMsat} msat).");
    }

    /// <summary>Determines the invoice status from settlement and expiry, relative to <paramref name="now"/>.</summary>
    internal static LightningInvoiceStatus DetermineStatus(bool settled, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (settled) return LightningInvoiceStatus.Paid;
        if (expiresAt < now) return LightningInvoiceStatus.Expired;
        return LightningInvoiceStatus.Unpaid;
    }

    /// <summary>Validates a Blink LUD-21 preimage against the payment hash: it must be 64 hex chars
    /// and SHA256(preimage) must equal the (natural-order hex) payment hash. Returns the preimage when
    /// valid, otherwise null.</summary>
    internal static string? ValidatePreimage(string paymentHash, string? preimage)
    {
        var normalizedHash = paymentHash?.Trim().ToLowerInvariant();
        if (preimage is not { Length: 64 } || !IsHex(preimage) || normalizedHash is not { Length: 64 })
            return null;
        try
        {
            var preimageBytes = Encoders.Hex.DecodeData(preimage);
            var computedHex = Encoders.Hex.EncodeData(Hashes.SHA256(preimageBytes));
            return computedHex.Equals(normalizedHash, StringComparison.OrdinalIgnoreCase) ? preimage : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Computes the next per-invoice back-off after a verify failure:
    /// delay = min(pollInterval * 2^errors, maxBackoff), with ±20% jitter to avoid lockstep retries
    /// when many invoices fail against the same degraded verify endpoint at once (F5).
    /// Returns the incremented error count and delay.</summary>
    internal static (int Errors, TimeSpan Delay) NextBackoff(int previousErrors, TimeSpan pollInterval, TimeSpan maxBackoff)
    {
        var errors = previousErrors + 1;
        var delayMs = Math.Min(pollInterval.TotalMilliseconds * Math.Pow(2, errors), maxBackoff.TotalMilliseconds);
        delayMs = ApplyJitter(delayMs);
        return (errors, TimeSpan.FromMilliseconds(delayMs));
    }

    /// <summary>Applies ±20% random jitter to a delay (milliseconds), never below 1ms.</summary>
    internal static double ApplyJitter(double delayMs)
    {
        var jitter = 1.0 + (Random.Shared.NextDouble() * 0.4 - 0.2); // [0.8, 1.2)
        return Math.Max(1.0, delayMs * jitter);
    }

    /// <summary>Age-stepped minimum poll interval for a tracked invoice (F3): poll fresh invoices
    /// fast for quick settlement feedback, then back off as the invoice ages so a long-lived unpaid
    /// invoice is not hammered every few seconds for its whole expiry window.</summary>
    internal static TimeSpan MinIntervalForAge(TimeSpan age)
        => age < TimeSpan.FromMinutes(2) ? TimeSpan.FromSeconds(3)
         : age < TimeSpan.FromMinutes(10) ? TimeSpan.FromSeconds(10)
         : TimeSpan.FromSeconds(30);

    /// <summary>The outcome of a single LUD-21 verify poll, so the poll loop can distinguish a real
    /// invoice status from a transport/HTTP failure (which must trigger back-off) and from an explicit
    /// "not found" (which, after confirmation, should evict the invoice) — without conflating them.</summary>
    internal enum VerifyOutcome
    {
        /// <summary>verify returned a usable status (settled/unpaid/expired).</summary>
        Status,
        /// <summary>HTTP error / timeout / unparseable body. Transient — the poller should back off.</summary>
        TransportError,
        /// <summary>verify explicitly returned status=ERROR (the server has no such invoice).</summary>
        NotFound,
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = new())
    {
        var (outcome, invoice) = await PollVerify(invoiceId, cancellation);

        // BTCPay's poller drops an invoice from monitoring when GetInvoice returns null, and it only
        // re-seeds monitored invoices on restart. So for a transient transport error we must NOT return
        // null; return a minimal Unpaid so the invoice stays tracked and is retried. (BTCPay
        // short-circuits Unpaid before reading amount/bolt11 fields, so this is safe.)
        if (outcome == VerifyOutcome.TransportError && invoice is null)
            return new LightningInvoice { Id = invoiceId, PaymentHash = invoiceId, Status = LightningInvoiceStatus.Unpaid };

        // NotFound => return null so BTCPay stops monitoring this invoice.
        return invoice;
    }

    /// <summary>Polls the LUD-21 verify URL for an invoice and reports the outcome. On TransportError the
    /// returned invoice may be null (no cached bolt11) or a synthetic Unpaid; on NotFound it is null; on
    /// Status it is the built invoice. Shared by GetInvoice and the settlement poll loop.</summary>
    internal async Task<(VerifyOutcome Outcome, LightningInvoice? Invoice)> PollVerify(
        string invoiceId, CancellationToken cancellation = new())
    {
        _tracked.TryGetValue(invoiceId, out var tracked);

        // Resolve the verify URL statelessly. On a fresh client instance (BTCPay's listener/poller)
        // there is no tracked invoice, so derive the verify origin from LNURL metadata.
        string verifyUrl;
        if (tracked?.VerifyUrl is { } tv)
            verifyUrl = tv;
        else
            verifyUrl = $"{await GetVerifyOrigin(cancellation)}/verify/{invoiceId}";

        JObject? json = null;
        bool transportError = false;
        try
        {
            using var resp = await _httpClient.GetAsync(verifyUrl, cancellation);
            var body = await resp.Content.ReadAsStringAsync(cancellation);
            if (resp.IsSuccessStatusCode)
                json = JObject.Parse(body);
            else
                transportError = true;
        }
        catch (Exception e)
        {
            transportError = true;
            _logger.LogDebug(e, "Blink LUD-21 verify request failed for {PaymentHash}", invoiceId);
        }

        // Explicit "not found" from the verify endpoint => the invoice genuinely does not exist.
        if (json?["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogDebug("Blink verify returned ERROR for {PaymentHash}: {Reason}", invoiceId,
                json["reason"]?.Value<string>());
            return (VerifyOutcome.NotFound, null);
        }

        var pr = tracked?.Bolt11 ?? json?["pr"]?.Value<string>();
        if (pr is null)
        {
            // No usable bolt11. On a transport error there is no status to report (invoice stays
            // tracked); on a 2xx with no pr and no cached bolt11 there is nothing to build.
            return transportError
                ? (VerifyOutcome.TransportError, null)
                : (VerifyOutcome.NotFound, null);
        }

        if (transportError)
            return (VerifyOutcome.TransportError, null);

        var settled = json?["settled"]?.Value<bool>() ?? false;
        var preimage = json?["preimage"]?.Value<string>();
        var expiresAt = tracked?.ExpiresAt ?? BOLT11PaymentRequest.Parse(pr, _network).ExpiryDate;
        var t = new TrackedInvoice(pr, verifyUrl, expiresAt)
        {
            CreatedAt = tracked?.CreatedAt ?? DateTimeOffset.UtcNow
        };
        return (VerifyOutcome.Status, BuildInvoice(invoiceId, t, settled, preimage));
    }

    private LightningInvoice BuildInvoice(string paymentHash, TrackedInvoice tracked, bool settled, string? preimage)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(tracked.Bolt11, _network);
        var status = DetermineStatus(settled, tracked.ExpiresAt, DateTimeOffset.UtcNow);

        // The authoritative payment hash is the verify key (the `paymentHash` argument = BTCPay's
        // invoice Id), NOT the hash parsed from the returned `pr`. blink-lnurl-server's verify
        // endpoint has been observed to return a `pr` whose payment hash can differ from the verify
        // key, so we validate the preimage against `paymentHash`.
        //
        // Compare as hex STRINGS to avoid NBitcoin's byte-order pitfalls: new uint256(hexString)
        // reverses bytes (display order) while new uint256(byte[]) does not, so comparing a
        // SHA256(byte[]) uint256 against a uint256 parsed from a hex string never matches.
        var normalizedPaymentHash = paymentHash.Trim().ToLowerInvariant();

        // Only trust the parsed BOLT11 (for BOLT11/Amount) if it actually corresponds to the
        // authoritative payment hash. Otherwise the `pr` is a mismatched fallback from verify and we
        // must not report it to BTCPay. The tracked bolt11 (created by us) always matches.
        bool bolt11Matches = bolt11.PaymentHash?.ToString().Equals(normalizedPaymentHash,
            StringComparison.OrdinalIgnoreCase) == true;
        var reportedBolt11 = bolt11Matches ? tracked.Bolt11 : null;
        var reportedAmount = bolt11Matches ? bolt11.MinimumAmount : null;

        // BTCPay validates the preimage: it must be 64 hex chars and SHA256(preimage)==paymentHash.
        // If invalid, drop it (the payment is still recorded, just without a preimage).
        var validPreimage = settled ? ValidatePreimage(paymentHash, preimage) : null;
        if (settled && preimage is not null && validPreimage is null)
            _logger.LogWarning("Blink preimage for {PaymentHash} did not validate against the payment hash; discarding.", paymentHash);

        return new LightningInvoice
        {
            Id = paymentHash,
            PaymentHash = paymentHash,
            BOLT11 = reportedBolt11,
            // Amount is parsed from the BOLT11; BTCPay requires a non-null amount to record payment.
            Amount = reportedAmount,
            AmountReceived = settled ? reportedAmount : null,
            Status = status,
            Preimage = validPreimage,
            PaidAt = settled ? DateTimeOffset.UtcNow : null,
            ExpiresAt = tracked.ExpiresAt
        };
    }

    internal static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    public async Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new())
    {
        return await GetInvoice(paymentHash.ToString(), cancellation);
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new())
    {
        return ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = new())
    {
        // No server-side invoice list is available for non-custodial accounts; only in-memory tracked
        // invoices. We report these as Unpaid (settled=false) without issuing a verify call per invoice:
        // settlement is authoritatively driven through GetInvoice / WaitInvoice, not ListInvoices, and
        // settled invoices are pruned from the registry by the poll loop, so the only misreport window
        // is the brief interval between a verify returning Paid and the poll loop's TryRemove. That
        // window is harmless for BTCPay's payment detection, so we keep this cheap and avoid N HTTP calls.
        var invoices = _tracked
            .Select(kv => BuildInvoice(kv.Key, kv.Value, false, null))
            .Where(i => request.PendingOnly is not true || i.Status == LightningInvoiceStatus.Unpaid)
            .ToArray();
        return Task.FromResult(invoices);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new())
    {
        // F4: a single shared poll loop per lightning address, ref-counted across all listeners.
        // Previously every Listen() spawned an independent PollLoop over the same shared registry,
        // multiplying the /verify/* request rate by the number of live listeners.
        var poller = SharedVerifyPoller.Acquire(_lightningAddress, this, _logger);
        return new BlinkLnAddressListener(poller);
    }

    /// <summary>
    /// One shared verify-polling loop per lightning address (static registry, ref-counted). Polls the
    /// LUD-21 verify URLs of tracked, unpaid invoices and broadcasts settlements to all listeners.
    ///
    /// Fixes vs. the previous per-listener loop:
    ///  - F1: transport/HTTP errors trigger capped exponential back-off (they are no longer swallowed
    ///        and mistaken for success), so a degraded blink-lnurl-server is not hammered.
    ///  - F2: invoices the server reports as not-found are evicted after a few confirmations instead of
    ///        being polled forever.
    ///  - F3: the per-invoice poll interval widens as the invoice ages (fresh = fast, stale = slow).
    ///  - F4: one loop per address regardless of how many times Listen() is called.
    ///
    /// Spark settlement is delivered to blink-lnurl-server via an SSP webhook, so detection may lag the
    /// actual payment by a few seconds; there is no websocket for non-custodial accounts.
    /// </summary>
    public sealed class SharedVerifyPoller
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

        // Evict an invoice only after this many consecutive not-found (status=ERROR) responses, to
        // tolerate the brief race between invoice creation and the invoice becoming visible to verify.
        internal const int NotFoundEvictionThreshold = 3;

        private static readonly ConcurrentDictionary<string, SharedVerifyPoller> _byAddress = new();

        private readonly string _address;
        private readonly BlinkLnAddressLightningClient _client;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pollTask;
        private int _refCount;

        // Per-invoice poll scheduling: consecutive transport errors, consecutive not-founds, and the
        // earliest next attempt (covers both error back-off and the age-stepped steady-state interval).
        private readonly System.Collections.Generic.Dictionary<string, InvoicePollState> _state = new();

        private sealed class InvoicePollState
        {
            public int Errors;
            public int NotFounds;
            public DateTimeOffset NextAttempt;
        }

        /// <summary>Raised when the poller observes an invoice settle. Listeners filter to their own.</summary>
        public event Action<LightningInvoice>? Settled;

        private SharedVerifyPoller(string address, BlinkLnAddressLightningClient client, ILogger logger)
        {
            _address = address;
            _client = client;
            _logger = logger;
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
        }

        /// <summary>Returns the shared poller for the address, creating and starting it on first use.</summary>
        public static SharedVerifyPoller Acquire(string address, BlinkLnAddressLightningClient client, ILogger logger)
        {
            return _byAddress.AddOrUpdate(
                address,
                _ => { var p = new SharedVerifyPoller(address, client, logger); p._refCount = 1; return p; },
                (_, existing) => { Interlocked.Increment(ref existing._refCount); return existing; });
        }

        /// <summary>Releases a listener's hold; stops and unregisters the loop when the last one leaves.</summary>
        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) > 0)
                return;
            _byAddress.TryRemove(_address, out _);
            _cts.Cancel();
            try { _pollTask.Wait(TimeSpan.FromSeconds(5)); }
            catch { /* cancellation / bounded wait */ }
            _cts.Dispose();
        }

        /// <summary>Number of live listeners holding this poller (exposed for tests).</summary>
        internal int RefCount => Volatile.Read(ref _refCount);

        /// <summary>How many shared pollers currently exist across all addresses (exposed for tests).</summary>
        internal static int ActivePollerCount => _byAddress.Count;

        private async Task PollLoop(CancellationToken cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var kv in _client._tracked.ToArray())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var (paymentHash, tracked) = (kv.Key, kv.Value);

                        var st = _state.TryGetValue(paymentHash, out var s) ? s : (_state[paymentHash] = new InvoicePollState());

                        // F1/F3: skip while backing off (after errors) or within this invoice's
                        // age-stepped minimum interval.
                        if (now < st.NextAttempt)
                            continue;

                        // A settled/expired invoice observed via a prior poll is pruned below; here we
                        // only prune on pure time-based expiry without needing a verify response.
                        try
                        {
                            var (outcome, invoice) = await _client.PollVerify(paymentHash, cancellation);
                            switch (outcome)
                            {
                                case VerifyOutcome.TransportError:
                                {
                                    // F1: a real failure => back off (was previously swallowed as success).
                                    st.NotFounds = 0;
                                    var (errors, delay) = NextBackoff(st.Errors, PollInterval, MaxBackoff);
                                    st.Errors = errors;
                                    st.NextAttempt = DateTimeOffset.UtcNow.Add(delay);
                                    break;
                                }
                                case VerifyOutcome.NotFound:
                                {
                                    // F2: evict after consecutive not-founds instead of polling forever.
                                    st.Errors = 0;
                                    st.NotFounds++;
                                    if (st.NotFounds >= NotFoundEvictionThreshold)
                                    {
                                        RemoveTracked(paymentHash);
                                        break;
                                    }
                                    // Not a transport error, but slow down re-checks of a missing invoice.
                                    st.NextAttempt = DateTimeOffset.UtcNow.Add(MaxBackoff);
                                    break;
                                }
                                case VerifyOutcome.Status:
                                {
                                    // Success: clear failure state and apply the age-stepped steady-state interval (F3).
                                    st.Errors = 0;
                                    st.NotFounds = 0;
                                    var age = DateTimeOffset.UtcNow - tracked.CreatedAt;
                                    var intervalMs = ApplyJitter(MinIntervalForAge(age).TotalMilliseconds);
                                    st.NextAttempt = DateTimeOffset.UtcNow.Add(TimeSpan.FromMilliseconds(intervalMs));

                                    if (invoice is null)
                                        break;
                                    if (invoice.Status == LightningInvoiceStatus.Paid)
                                    {
                                        RemoveTracked(paymentHash);
                                        Settled?.Invoke(invoice);
                                    }
                                    else if (invoice.Status == LightningInvoiceStatus.Expired ||
                                             tracked.ExpiresAt < DateTimeOffset.UtcNow)
                                    {
                                        RemoveTracked(paymentHash);
                                    }
                                    break;
                                }
                            }
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            // Defensive back-off for any unexpected throw escaping PollVerify.
                            var (errors, delay) = NextBackoff(st.Errors, PollInterval, MaxBackoff);
                            st.Errors = errors;
                            st.NextAttempt = DateTimeOffset.UtcNow.Add(delay);
                            _logger.LogDebug(e, "Error polling Blink invoice {PaymentHash} (attempt {Errors}); backing off {DelayMs}ms",
                                paymentHash, errors, delay.TotalMilliseconds);
                        }
                    }

                    // Prune poll state for invoices no longer tracked so _state stays bounded.
                    if (_state.Count > 0)
                    {
                        var live = new HashSet<string>(_client._tracked.Keys);
                        foreach (var key in _state.Keys.ToArray())
                            if (!live.Contains(key))
                                _state.Remove(key);
                    }

                    await Task.Delay(PollInterval, cancellation);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Blink shared verify poll loop for {Address} terminated unexpectedly", _address);
            }
        }

        private void RemoveTracked(string paymentHash)
        {
            _client.RemoveTrackedInvoice(paymentHash);
            _state.Remove(paymentHash);
        }
    }

    /// <summary>
    /// A cheap per-Listen() subscriber over the shared poller's settlement broadcast. It holds no poll
    /// loop of its own — the single SharedVerifyPoller per address does the polling and fans out.
    /// </summary>
    public sealed class BlinkLnAddressListener : ILightningInvoiceListener
    {
        private readonly SharedVerifyPoller _poller;
        private readonly Channel<LightningInvoice> _channel = Channel.CreateUnbounded<LightningInvoice>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<LightningInvoice> _handler;

        public BlinkLnAddressListener(SharedVerifyPoller poller)
        {
            _poller = poller;
            _handler = inv => _channel.Writer.TryWrite(inv);
            _poller.Settled += _handler;
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
            return await _channel.Reader.ReadAsync(linked.Token);
        }

        public void Dispose()
        {
            _poller.Settled -= _handler;
            _cts.Cancel();
            _channel.Writer.TryComplete();
            _cts.Dispose();
            _poller.Release();
        }
    }

    public async Task<ValidationResult?> Validate()
    {
        if (_network != Network.Main)
            return new ValidationResult(
                "Blink non-custodial (Spark) accounts are only available on mainnet (blink.sv).");
        try
        {
            var metadata = await FetchLnurlMetadata(CancellationToken.None);
            if (metadata["callback"]?.Value<string>() is null or "")
                return new ValidationResult("The Blink lightning address did not return a valid LNURL-pay callback.");
        }
        catch (Exception e)
        {
            if (_usd)
                return new ValidationResult(
                    $"Could not validate the USD (USDB) Blink address. Non-custodial USD receive may not yet be supported by Blink. ({e.Message})");
            return new ValidationResult(e.Message);
        }

        return ValidationResult.Success;
    }

    private const string ReceiveOnlyMessage =
        "This is a Blink non-custodial (Spark) account configured for receiving only. Sending, balance and channel operations require the wallet seed and are not supported.";

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<LightningPayment?> GetPayment(string paymentHash, CancellationToken cancellation = new())
        => Task.FromResult<LightningPayment?>(null);

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = new())
        => Task.FromResult(Array.Empty<LightningPayment>());

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = new())
        => Task.FromResult(Array.Empty<LightningPayment>());

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = new())
    {
        RemoveTrackedInvoice(invoiceId);
        return Task.CompletedTask;
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest,
        CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = new())
        => throw new NotSupportedException(ReceiveOnlyMessage);

    public string? DisplayName => "Blink (non-custodial)";
    public Uri? ServerUri => new($"https://{_domain}");
}
