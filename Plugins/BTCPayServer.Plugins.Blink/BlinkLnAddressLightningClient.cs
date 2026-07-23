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

    // Custodial invoices are minted via the public GraphQL API and settle through
    // lnInvoicePaymentStatusByHash (ledger-aware); non-custodial invoices are proxied from the LNURL
    // server and settle through the LUD-21 verify URL. VerifyUrl is null for custodial invoices.
    private record TrackedInvoice(string Bolt11, string? VerifyUrl, DateTimeOffset ExpiresAt, bool Custodial = false);

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
    }

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

    private Uri GraphQLEndpoint => BlinkGraphQLPublicClient.GraphQLEndpointForDomain(_domain);

    /// <summary>
    /// Resolves whether this lightning address points at a custodial Galoy account (in which case we
    /// mint invoices directly via the public GraphQL API) or a non-custodial Spark account (LNURL proxy).
    /// The result is cached by <see cref="BlinkGraphQLPublicClient"/>. On any transport error we fall
    /// back to non-custodial (LNURL) behaviour, which works for both account types.
    /// </summary>
    private async Task<BlinkGraphQLPublicClient.AccountInfo> ResolveAccount(CancellationToken cancellation)
    {
        try
        {
            return await BlinkGraphQLPublicClient.ResolveAccountAsync(
                _httpClient, GraphQLEndpoint, _username, _usd, cancellation);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not resolve Blink account type for {Address}; assuming non-custodial (LNURL).",
                _lightningAddress);
            return new BlinkGraphQLPublicClient.AccountInfo(
                BlinkGraphQLPublicClient.AccountKind.NonCustodial, null, null);
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
        // BTCPay allows a null amount for top-up/amountless invoices, but LNURL-pay is inherently
        // amount-driven, so we require a concrete amount and fail with a clear message otherwise.
        // BTCPay sends LightMoney.Zero (not null) for InvoiceType.TopUp, so reject <= 0 too.
        //
        // This is expected for top-up invoices and NOT a failure: BTCPay catches this per payment
        // method and falls back to serving the LNURL QR, where the payer's wallet supplies the amount
        // and the callback creates the invoice for that amount. BTCPay records the fallback in the
        // invoice event log (shown in red) - the top-up invoice can still be paid via LNURL. Note this
        // applies to any Blink lightning-address connection, including one pointing at a custodial
        // account, because such connections only receive via LNURL-pay and cannot mint a bolt11 here.
        // (Checked first so the routine top-up rejection avoids an unnecessary LNURL metadata fetch.)
        if (createInvoiceRequest.Amount is null || createInvoiceRequest.Amount.MilliSatoshi <= 0)
            throw new NotSupportedException(
                "Blink lightning-address connections cannot create an amountless (top-up) bolt11 invoice. " +
                "This is expected: BTCPay falls back to the LNURL payment method for top-up invoices, " +
                "where the payer chooses the amount in their wallet.");

        // Custodial Galoy accounts: mint the invoice directly via the public GraphQL API instead of
        // proxying the LNURL server. This commits the BOLT11 to BTCPay's own description hash (so the
        // store description shows in the payer's wallet) and, crucially, avoids serving a
        // "text/identifier" that would make the Blink mobile app pay intraledger and bypass BTCPay's
        // invoice entirely. Settlement is detected via lnInvoicePaymentStatusByHash (ledger-aware).
        var account = await ResolveAccount(cancellation);
        if (account.Kind == BlinkGraphQLPublicClient.AccountKind.Custodial && account.WalletId is { } walletId)
        {
            return await CreateCustodialInvoice(createInvoiceRequest, walletId, cancellation);
        }

        // Non-custodial (Spark) path: only now do we need the LNURL-pay metadata/callback.
        var metadata = await FetchLnurlMetadata(cancellation);
        var callback = metadata["callback"]?.Value<string>();
        if (string.IsNullOrEmpty(callback))
            throw new Exception("LNURL-pay response is missing a callback URL.");

        var amountMsat = createInvoiceRequest.Amount.MilliSatoshi;
        var min = metadata["minSendable"]?.Value<long>() ?? 1;
        var max = metadata["maxSendable"]?.Value<long>() ?? long.MaxValue;
        ValidateAmountBounds(amountMsat, min, max);

        var callbackUri = new UriBuilder(callback);
        var query = new StringBuilder(callbackUri.Query.TrimStart('?'));
        if (query.Length > 0) query.Append('&');
        query.Append("amount=").Append(amountMsat);
        // Pass along a description/comment when the endpoint allows comments (LUD-12). But when
        // DescriptionHashOnly is set (the LNURL-pay callback path), Description is BTCPay's serialized
        // LNURL metadata JSON, not a human-readable comment - forwarding that blob as a LUD-12 comment
        // is wrong (and may be rejected by the LNURL server), so skip it in that case.
        var commentAllowed = metadata["commentAllowed"]?.Value<int>() ?? 0;
        if (commentAllowed > 0 && !createInvoiceRequest.DescriptionHashOnly &&
            !string.IsNullOrEmpty(createInvoiceRequest.Description))
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

    /// <summary>
    /// Mints a fixed-amount invoice for a custodial Galoy account via the public GraphQL API,
    /// committing to BTCPay's own description hash so the returned BOLT11's h-tag matches the metadata
    /// BTCPay serves to the payer. The invoice is tracked as custodial and settles via
    /// lnInvoicePaymentStatusByHash.
    /// </summary>
    private async Task<LightningInvoice> CreateCustodialInvoice(CreateInvoiceParams createInvoiceRequest,
        string walletId, CancellationToken cancellation)
    {
        var amountSat = (long)createInvoiceRequest.Amount!.ToUnit(LightMoneyUnit.Satoshi);

        // Determine the description hash to commit to. In the LNURL callback path BTCPay passes the
        // serialized metadata as Description with DescriptionHashOnly=true; hash it ourselves. If an
        // explicit DescriptionHash is provided, prefer it.
        var descriptionHashHex = ComputeDescriptionHashHex(createInvoiceRequest);
        var memo = descriptionHashHex is null ? createInvoiceRequest.Description : null;
        var expiresIn = Math.Max(1, (int)createInvoiceRequest.Expiry.TotalMinutes);

        var (pr, _) = await BlinkGraphQLPublicClient.CreateInvoiceOnBehalfAsync(
            _httpClient, GraphQLEndpoint, walletId, amountSat, descriptionHashHex, memo, expiresIn, _usd,
            cancellation);

        var bolt11 = BOLT11PaymentRequest.Parse(pr, _network);

        // Defensive checks mirroring the LNURL path: the minted invoice must match the requested amount
        // and, when a description hash was requested, carry that exact hash.
        if (bolt11.MinimumAmount != createInvoiceRequest.Amount)
            throw new Exception(
                $"Blink minted an invoice for {bolt11.MinimumAmount.MilliSatoshi} msat but " +
                $"{createInvoiceRequest.Amount.MilliSatoshi} msat was requested.");
        if (createInvoiceRequest.DescriptionHash is { } dh && dh != bolt11.DescriptionHash)
            throw new Exception("Blink minted an invoice with a mismatched description hash.");

        var paymentHash = bolt11.PaymentHash?.ToString() ?? throw new Exception("Invoice has no payment hash.");
        var expiresAt = bolt11.ExpiryDate;
        _tracked[paymentHash] = new TrackedInvoice(pr, VerifyUrl: null, expiresAt, Custodial: true);

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

    /// <summary>Returns the 32-byte hex description hash to commit to, or null when none applies.
    /// Prefers an explicit <c>DescriptionHash</c>; otherwise, when <c>DescriptionHashOnly</c> is set,
    /// hashes the (metadata) Description - matching how BTCPay's LNURL endpoint computes the hash the
    /// payer's wallet will verify.</summary>
    internal static string? ComputeDescriptionHashHex(CreateInvoiceParams createInvoiceRequest)
    {
        if (createInvoiceRequest.DescriptionHash is { } dh)
            return dh.ToString();
        if (createInvoiceRequest.DescriptionHashOnly && !string.IsNullOrEmpty(createInvoiceRequest.Description))
            return Encoders.Hex.EncodeData(Hashes.SHA256(Encoding.UTF8.GetBytes(createInvoiceRequest.Description!)));
        return null;
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
    /// delay = min(pollInterval * 2^errors, maxBackoff). Returns the incremented error count and delay.</summary>
    internal static (int Errors, TimeSpan Delay) NextBackoff(int previousErrors, TimeSpan pollInterval, TimeSpan maxBackoff)
    {
        var errors = previousErrors + 1;
        var delayMs = Math.Min(pollInterval.TotalMilliseconds * Math.Pow(2, errors), maxBackoff.TotalMilliseconds);
        return (errors, TimeSpan.FromMilliseconds(delayMs));
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = new())
    {
        _tracked.TryGetValue(invoiceId, out var tracked);

        // Custodial invoices settle through the ledger-aware GraphQL status query, not LUD-21 verify.
        // A tracked custodial entry is authoritative; on a fresh listener instance (no tracked entry)
        // fall back to resolving the account type so we still pick the right settlement source.
        var isCustodial = tracked?.Custodial ??
            (await ResolveAccount(cancellation)).Kind == BlinkGraphQLPublicClient.AccountKind.Custodial;
        if (isCustodial)
            return await GetCustodialInvoice(invoiceId, tracked, cancellation);

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

    /// <summary>
    /// Settlement for custodial invoices via the ledger-aware <c>lnInvoicePaymentStatusByHash</c> query.
    /// This reports PAID even when Galoy smart-settles a Blink-to-Blink payment intraledger, which the
    /// LUD-21 verify endpoint would miss. No preimage is available from this query, so payments are
    /// recorded without one (BTCPay supports this).
    /// </summary>
    private async Task<LightningInvoice?> GetCustodialInvoice(string invoiceId, TrackedInvoice? tracked,
        CancellationToken cancellation)
    {
        string? status;
        try
        {
            status = await BlinkGraphQLPublicClient.GetPaymentStatusByHashAsync(
                _httpClient, GraphQLEndpoint, invoiceId, cancellation);
        }
        catch (Exception e)
        {
            // Transient error: keep the invoice tracked (return Unpaid rather than null) so BTCPay's
            // poller retries instead of dropping it. Matches the LUD-21 path's behaviour.
            _logger.LogDebug(e, "Blink GraphQL status poll failed for {PaymentHash}", invoiceId);
            return new LightningInvoice
            {
                Id = invoiceId,
                PaymentHash = invoiceId,
                Status = LightningInvoiceStatus.Unpaid
            };
        }

        if (status is null)
            return null; // unknown invoice

        var mapped = BlinkLightningClient.MapInvoiceStatus(status);
        var settled = mapped == LightningInvoiceStatus.Paid;

        LightMoney? amount = null;
        string? bolt11 = tracked?.Bolt11;
        DateTimeOffset? expiresAt = tracked?.ExpiresAt;
        if (bolt11 is not null)
        {
            var parsed = BOLT11PaymentRequest.Parse(bolt11, _network);
            amount = parsed.MinimumAmount;
            expiresAt ??= parsed.ExpiryDate;
        }

        // Respect expiry the same way DetermineStatus does for the LUD-21 path.
        var finalStatus = DetermineStatus(settled, expiresAt ?? DateTimeOffset.MaxValue, DateTimeOffset.UtcNow);

        return new LightningInvoice
        {
            Id = invoiceId,
            PaymentHash = invoiceId,
            BOLT11 = bolt11,
            Amount = amount,
            AmountReceived = settled ? amount : null,
            Status = finalStatus,
            PaidAt = settled ? DateTimeOffset.UtcNow : null,
            ExpiresAt = expiresAt ?? default
        };
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
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
        private readonly BlinkLnAddressLightningClient _client;
        private readonly ILogger _logger;
        private readonly Channel<LightningInvoice> _channel = Channel.CreateUnbounded<LightningInvoice>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pollTask;

        // Per-invoice error back-off: number of consecutive failures and the earliest next attempt.
        private readonly System.Collections.Generic.Dictionary<string, (int Errors, DateTimeOffset NextAttempt)>
            _backoff = new();

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
                    var now = DateTimeOffset.UtcNow;
                    foreach (var kv in _client._tracked.ToArray())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var (paymentHash, tracked) = (kv.Key, kv.Value);

                        // Skip this invoice while it is backing off after repeated failures.
                        if (_backoff.TryGetValue(paymentHash, out var b) && now < b.NextAttempt)
                            continue;

                        try
                        {
                            var invoice = await _client.GetInvoice(paymentHash, cancellation);
                            _backoff.Remove(paymentHash); // success resets back-off
                            if (invoice is null) continue;
                            if (invoice.Status == LightningInvoiceStatus.Paid)
                            {
                                RemoveTracked(paymentHash);
                                await _channel.Writer.WriteAsync(invoice, cancellation);
                            }
                            else if (invoice.Status == LightningInvoiceStatus.Expired ||
                                     tracked.ExpiresAt < DateTimeOffset.UtcNow)
                            {
                                RemoveTracked(paymentHash);
                            }
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            // Capped exponential back-off so a degraded blink-lnurl-server is not
                            // hammered every 3s per invoice.
                            var prevErrors = _backoff.TryGetValue(paymentHash, out var prev) ? prev.Errors : 0;
                            var (errors, delay) = NextBackoff(prevErrors, PollInterval, MaxBackoff);
                            _backoff[paymentHash] = (errors, DateTimeOffset.UtcNow.Add(delay));
                            _logger.LogDebug(e, "Error polling Blink invoice {PaymentHash} (attempt {Errors}); backing off {DelayMs}ms",
                                paymentHash, errors, delay.TotalMilliseconds);
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

        private void RemoveTracked(string paymentHash)
        {
            _client.RemoveTrackedInvoice(paymentHash);
            _backoff.Remove(paymentHash);
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _cts.Token);
            return await _channel.Reader.ReadAsync(linked.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            // Wait (bounded) for the poll loop to observe cancellation before completing the channel,
            // so an in-flight WriteAsync cannot race TryComplete and surface a ChannelClosedException.
            try { _pollTask.Wait(TimeSpan.FromSeconds(5)); }
            catch { /* AggregateException from cancellation or timeout: ignore on dispose */ }
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
