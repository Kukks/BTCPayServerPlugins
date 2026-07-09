#nullable enable
using System;
using System.Collections.Concurrent;
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

    private record TrackedInvoice(string Bolt11, string? VerifyUrl, DateTimeOffset ExpiresAt);

    // BTCPay creates SEPARATE client instances for creating invoices and for its payment
    // listener/poller. In-memory per-instance state would therefore not be visible to the listener.
    // We share tracked invoices across all instances of the same lightning address via a static,
    // address-keyed registry so the listener can poll invoices created by another instance.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackedInvoice>>
        _sharedTracked = new();

    private ConcurrentDictionary<string, TrackedInvoice> _tracked =>
        _sharedTracked.GetOrAdd(_lightningAddress, _ => new ConcurrentDictionary<string, TrackedInvoice>());

    // The origin (scheme://host[:port]) of the LNURL-pay callback, which is also the host that
    // serves the LUD-21 verify endpoint (e.g. https://lnurl.blink.sv). It is NOT the lightning
    // address domain (blink.sv). Cached so a stateless GetInvoice can rebuild the verify URL.
    private string? _verifyOrigin;

    public BlinkLnAddressLightningClient(string lightningAddress, bool usd, Network network,
        HttpClient httpClient, ILogger logger)
    {
        _lightningAddress = lightningAddress;
        var parts = lightningAddress.Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new FormatException($"Invalid Blink lightning address '{lightningAddress}'");
        _username = parts[0];
        _domain = parts[1];
        _usd = usd;
        _network = network;
        _httpClient = httpClient;
        _logger = logger;
    }

    private Uri LnurlpMetadataUri
    {
        get
        {
            // USDB uses a "+usd" wallet modifier on the local part (blink-lnurl-server identifier parsing).
            var localPart = _usd ? $"{_username}+usd" : _username;
            var scheme = _domain.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                         _domain.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
                ? "http"
                : "https";
            return new Uri($"{scheme}://{_domain}/.well-known/lnurlp/{Uri.EscapeDataString(localPart)}");
        }
    }

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
        if (json["callback"]?.Value<string>() is { } cb && Uri.TryCreate(cb, UriKind.Absolute, out var cbUri))
            _verifyOrigin = cbUri.GetLeftPart(UriPartial.Authority);

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
        if (amountMsat < min)
            throw new Exception(
                $"Amount {amountMsat} msat is below the minimum accepted by this Blink address ({min} msat).");
        if (amountMsat > max)
            throw new Exception(
                $"Amount {amountMsat} msat is above the maximum accepted by this Blink address ({max} msat).");

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
        if (createInvoiceRequest.DescriptionHash is { } dh && bolt11.DescriptionHash is { } bh &&
            dh != bh)
            throw new Exception("Blink returned an invoice with a mismatched description hash.");

        var paymentHash = bolt11.PaymentHash?.ToString() ?? throw new Exception("Invoice has no payment hash.");
        var verifyUrl = json["verify"]?.Value<string>();
        if (!string.IsNullOrEmpty(verifyUrl) && Uri.TryCreate(verifyUrl, UriKind.Absolute, out var vu))
            _verifyOrigin = vu.GetLeftPart(UriPartial.Authority);
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
        return $"{_verifyOrigin}/verify/{paymentHash}";
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = new())
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
        // Only in that case do we return null (BTCPay will stop tracking it).
        if (json?["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogDebug("Blink verify returned ERROR for {PaymentHash}: {Reason}", invoiceId,
                json["reason"]?.Value<string>());
            return null;
        }

        var pr = tracked?.Bolt11 ?? json?["pr"]?.Value<string>();
        if (pr is null)
        {
            // Transient transport error on a fresh client (no cached bolt11). Do NOT return null:
            // BTCPay's poller drops an invoice from monitoring when GetInvoice returns null, and it
            // only re-seeds monitored invoices on restart. Instead return a minimal Unpaid invoice so
            // the invoice stays tracked and is retried. (BTCPay short-circuits Unpaid before reading
            // amount/bolt11 fields, so this is safe.)
            if (transportError)
                return new LightningInvoice
                {
                    Id = invoiceId,
                    PaymentHash = invoiceId,
                    Status = LightningInvoiceStatus.Unpaid
                };
            return null;
        }

        var settled = json?["settled"]?.Value<bool>() ?? false;
        var preimage = json?["preimage"]?.Value<string>();
        var expiresAt = tracked?.ExpiresAt ?? BOLT11PaymentRequest.Parse(pr, _network).ExpiryDate;
        var t = new TrackedInvoice(pr, verifyUrl, expiresAt);
        return BuildInvoice(invoiceId, t, settled, preimage);
    }

    private LightningInvoice BuildInvoice(string paymentHash, TrackedInvoice tracked, bool settled, string? preimage)
    {
        var bolt11 = BOLT11PaymentRequest.Parse(tracked.Bolt11, _network);
        LightningInvoiceStatus status;
        if (settled) status = LightningInvoiceStatus.Paid;
        else if (tracked.ExpiresAt < DateTimeOffset.UtcNow) status = LightningInvoiceStatus.Expired;
        else status = LightningInvoiceStatus.Unpaid;

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
        string? validPreimage = null;
        if (settled && preimage is { Length: 64 } && IsHex(preimage) && normalizedPaymentHash.Length == 64)
        {
            try
            {
                var preimageBytes = Encoders.Hex.DecodeData(preimage);
                var computedHex = Encoders.Hex.EncodeData(Hashes.SHA256(preimageBytes));
                if (computedHex.Equals(normalizedPaymentHash, StringComparison.OrdinalIgnoreCase))
                    validPreimage = preimage;
                else
                    _logger.LogWarning("Blink preimage for {PaymentHash} does not hash to the payment hash (got {Computed}); discarding.", paymentHash, computedHex);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Failed to validate Blink preimage for {PaymentHash}", paymentHash);
            }
        }

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

    private static bool IsHex(string s)
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
        // No server-side invoice list is available for non-custodial accounts; only in-memory tracked invoices.
        var invoices = _tracked
            .Select(kv => BuildInvoice(kv.Key, kv.Value, false, null))
            .Where(i => request.PendingOnly is not true || i.Status == LightningInvoiceStatus.Unpaid)
            .ToArray();
        return Task.FromResult(invoices);
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = new())
    {
        return new BlinkLnAddressListener(this, _logger);
    }

    /// <summary>
    /// Polls the LUD-21 verify URLs of tracked, unpaid invoices and yields them as they settle.
    /// Spark settlement is delivered to blink-lnurl-server via an SSP webhook, so detection may lag
    /// the actual payment by a few seconds; there is no websocket for non-custodial accounts.
    /// </summary>
    public class BlinkLnAddressListener : ILightningInvoiceListener
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
        private readonly BlinkLnAddressLightningClient _client;
        private readonly ILogger _logger;
        private readonly Channel<LightningInvoice> _channel = Channel.CreateUnbounded<LightningInvoice>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pollTask;

        public BlinkLnAddressListener(BlinkLnAddressLightningClient client, ILogger logger)
        {
            _client = client;
            _logger = logger;
            _pollTask = Task.Run(() => PollLoop(_cts.Token));
        }

        private async Task PollLoop(CancellationToken cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    foreach (var kv in _client._tracked.ToArray())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var (paymentHash, tracked) = (kv.Key, kv.Value);
                        try
                        {
                            var invoice = await _client.GetInvoice(paymentHash, cancellation);
                            if (invoice is null) continue;
                            if (invoice.Status == LightningInvoiceStatus.Paid)
                            {
                                _client._tracked.TryRemove(paymentHash, out _);
                                await _channel.Writer.WriteAsync(invoice, cancellation);
                            }
                            else if (invoice.Status == LightningInvoiceStatus.Expired ||
                                     tracked.ExpiresAt < DateTimeOffset.UtcNow)
                            {
                                _client._tracked.TryRemove(paymentHash, out _);
                            }
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            _logger.LogDebug(e, "Error polling Blink invoice {PaymentHash}", paymentHash);
                        }
                    }

                    await Task.Delay(PollInterval, cancellation);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _channel.Writer.TryComplete(e);
            }
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
            return await _channel.Reader.ReadAsync(linked.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _channel.Writer.TryComplete();
            _cts.Dispose();
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
        _tracked.TryRemove(invoiceId, out _);
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
