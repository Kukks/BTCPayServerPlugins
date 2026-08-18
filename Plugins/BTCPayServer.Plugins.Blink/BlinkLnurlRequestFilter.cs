#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Services.Invoices;
using LNURL;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Blink;

/// <summary>
/// Aligns BTCPay's served LNURL-pay parameters with Blink's when a store's BTC lightning backend is
/// a Blink non-custodial (Spark) lightning address.
///
/// Why this is required: for such a store the payable BOLT11 is minted by Blink's LNURL server (the
/// <see cref="BlinkLnAddressLightningClient"/> proxies it), and that invoice commits, via its BOLT11
/// <c>h</c> (description hash) tag, to <em>Blink's own</em> LNURL metadata. BTCPay by default serves
/// its <em>own</em> metadata (store name/description). LUD-06 requires the payer's wallet to check
/// that SHA256(served metadata) equals the invoice's <c>h</c> tag; the two differ, so strict wallets
/// (e.g. Phoenix, Blitz) refuse to pay. By mirroring Blink's metadata here the hashes match and the
/// payment succeeds. This also corrects the advertised min/max sendable to Blink's real limits.
///
/// Tradeoff: the payer's wallet then shows Blink's identity line ("Pay to user@blink.sv") rather than
/// the store description. This is unavoidable because the description hash is committed by Blink.
/// </summary>
public class BlinkLnurlRequestFilter : PluginHookFilter<LNURLPayRequest>
{
    public override string Hook => "modify-lnurlp-request";

    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BlinkLnurlRequestFilter> _logger;

    public BlinkLnurlRequestFilter(
        PaymentMethodHandlerDictionary handlers,
        IHttpClientFactory httpClientFactory,
        ILogger<BlinkLnurlRequestFilter> logger)
    {
        _handlers = handlers;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public override async Task<LNURLPayRequest> Execute(LNURLPayRequest arg)
    {
        try
        {
            if (arg is not StoreLNURLPayRequest { Store: { } store })
                return arg;

            // Resolve the store's BTC lightning connection string and detect a Blink ln-address.
            var lnPmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var configs = store.GetPaymentMethodConfigs<LightningPaymentMethodConfig>(_handlers, onlyEnabled: true);
            if (!configs.TryGetValue(lnPmi, out var lnConfig))
                return arg;
            var connectionString = lnConfig.GetExternalLightningUrl();
            if (!TryGetBlinkLnAddress(connectionString, out var lnAddress, out var usd))
                return arg;

            var (username, domain) = BlinkLnAddressLightningClient.ParseLightningAddress(lnAddress!);
            var metadataUri = BlinkLnAddressLightningClient.BuildLnurlpMetadataUri(username, domain, usd);

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await httpClient.GetAsync(metadataUri, CancellationToken.None);
            if (!resp.IsSuccessStatusCode)
                return arg;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());

            // Custodial accounts mint invoices directly via GraphQL committing to BTCPay's OWN metadata
            // (see BlinkLnAddressLightningClient.CreateCustodialInvoice), so we must NOT replace the
            // served metadata here - doing so would (a) break the description-hash match and (b) expose
            // a "text/identifier" that makes the Blink mobile app pay intraledger and bypass the invoice.
            // For non-custodial accounts the invoice is proxied from Blink's LNURL server and commits to
            // Blink's metadata, so mirroring is required for strict wallets to accept it.
            //
            // Resolve in its OWN try/catch and default to mirroring on failure: a resolution error must
            // degrade to the safe non-custodial behaviour (mirror), because the client's own resolver
            // also falls back to the LNURL proxy path whose invoices require mirroring. Skipping
            // mirroring on error would break strict-wallet payments for Spark addresses.
            var mirrorMetadata = true;
            try
            {
                var endpoint = BlinkGraphQLPublicClient.GraphQLEndpointForDomain(domain);
                var account = await BlinkGraphQLPublicClient.ResolveAccountAsync(
                    httpClient, endpoint, username, usd, CancellationToken.None);
                mirrorMetadata = account.Kind == BlinkGraphQLPublicClient.AccountKind.NonCustodial;
            }
            catch (Exception e)
            {
                _logger.LogDebug(e,
                    "Could not resolve Blink account type for {Address}; mirroring metadata (non-custodial default).",
                    lnAddress);
            }

            ApplyBlinkParameters(arg, json, mirrorMetadata);
            return arg;
        }
        catch (Exception e)
        {
            // Never break checkout because of this enhancement; the payment may still work for
            // wallets that do not enforce the LUD-06 description-hash commitment.
            _logger.LogWarning(e, "Failed to align LNURL-pay parameters with Blink; leaving BTCPay defaults.");
            return arg;
        }
    }

    /// <summary>Detects a Blink non-custodial (ln-address, no api-key) connection string and extracts
    /// its lightning address and USD flag. Mirrors <see cref="BlinkLightningConnectionStringHandler"/>.</summary>
    internal static bool TryGetBlinkLnAddress(string? connectionString, out string? lnAddress, out bool usd)
    {
        lnAddress = null;
        usd = false;
        if (string.IsNullOrEmpty(connectionString))
            return false;

        Dictionary<string, string> kv;
        try
        {
            kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            if (type != "blink")
                return false;
        }
        catch
        {
            return false;
        }

        if (!BlinkLightningConnectionStringHandler.IsLnAddressConnectionString(kv, out lnAddress) ||
            string.IsNullOrWhiteSpace(lnAddress))
            return false;

        if (!lnAddress!.Contains('@'))
            lnAddress = $"{lnAddress}@blink.sv";

        if (kv.TryGetValue("currency", out var currencyStr))
        {
            try { usd = BlinkLightningClient.ParseBlinkCurrency(currencyStr) == BlinkCurrency.USD; }
            catch (FormatException) { /* leave usd=false; invalid currency is handled elsewhere */ }
        }
        return true;
    }

    /// <summary>
    /// Narrows the sendable bounds to the intersection of BTCPay's and Blink's limits (only ever
    /// narrowed, never widened, so a fixed-amount invoice with min == max is preserved) and caps the
    /// allowed comment length to Blink's. When <paramref name="mirrorMetadata"/> is true (non-custodial
    /// accounts) it also overwrites the served metadata with Blink's, so the served-metadata hash
    /// matches the invoice's committed description hash. For custodial accounts metadata is left intact.
    /// </summary>
    internal static void ApplyBlinkParameters(LNURLPayRequest arg, JObject blinkMetadata, bool mirrorMetadata = true)
    {
        if (mirrorMetadata)
        {
            var metadata = blinkMetadata["metadata"]?.Value<string>();
            if (!string.IsNullOrEmpty(metadata))
                arg.Metadata = metadata;
        }

        var blinkMin = blinkMetadata["minSendable"]?.Value<long>() is { } bmin ? new LightMoney(bmin) : null;
        var blinkMax = blinkMetadata["maxSendable"]?.Value<long>() is { } bmax ? new LightMoney(bmax) : null;

        // Compute the intersection of [BTCPay.Min, BTCPay.Max] and [Blink.Min, Blink.Max]. If the two
        // ranges are DISJOINT (Blink's min exceeds BTCPay's max, or vice versa) there is no valid
        // amount, so leave BTCPay's advertised bounds untouched rather than fabricating a fixed amount
        // Blink would reject anyway. The callback's own ValidateAmountBounds still rejects any
        // out-of-range amount cleanly, and disjoint ranges are not expected in practice (Blink's real
        // minimum is 1 sat).
        var newMin = Max(arg.MinSendable, blinkMin);
        var newMax = Min(arg.MaxSendable, blinkMax);
        if (newMin is null || newMax is null || newMin <= newMax)
        {
            if (newMin is not null)
                arg.MinSendable = newMin;
            if (newMax is not null)
                arg.MaxSendable = newMax;
        }

        if (blinkMetadata["commentAllowed"]?.Value<int>() is { } blinkComment &&
            arg.CommentAllowed > blinkComment)
            arg.CommentAllowed = blinkComment;
    }

    /// <summary>Returns the larger of two possibly-null amounts (null is treated as "no bound").</summary>
    private static LightMoney? Max(LightMoney? a, LightMoney? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }

    /// <summary>Returns the smaller of two possibly-null amounts (null is treated as "no bound").</summary>
    private static LightMoney? Min(LightMoney? a, LightMoney? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a < b ? a : b;
    }
}
