#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Blink;

/// <summary>
/// Thin wrapper around Blink's public GraphQL API for the operations that do NOT require an API key
/// (see https://dev.blink.sv/api/no-api-key-operations):
/// <list type="bullet">
///   <item><c>accountDefaultWallet(username)</c> - resolves the default wallet of a username. Used to
///   detect whether a Blink lightning address points at a custodial Galoy account (resolves) or a
///   non-custodial Spark account (does not resolve).</item>
///   <item><c>lnInvoiceCreateOnBehalfOfRecipient</c> / <c>lnNoAmountInvoiceCreateOnBehalfOfRecipient</c>
///   - mints an invoice for a recipient wallet.</item>
///   <item><c>lnInvoicePaymentStatusByHash</c> - polls an invoice's settlement status. This is
///   ledger-aware, so it reports PAID even when Galoy smart-settles a Blink-to-Blink payment
///   intraledger (which never touches the LNURL LUD-21 verify endpoint).</item>
/// </list>
///
/// Detecting the account type this way lets the plugin mint custodial invoices directly (committing
/// to BTCPay's own description metadata) instead of proxying the LNURL-pay server. That both restores
/// the store description in the payer's wallet and - critically - avoids the Blink mobile app's
/// "pay intraledger if the LNURL identifier is one of our own domains" shortcut, which otherwise
/// bypasses BTCPay's invoice entirely and leaves the payment undetected.
/// </summary>
internal static class BlinkGraphQLPublicClient
{
    public enum AccountKind
    {
        /// <summary>Username resolves to a Galoy wallet: a custodial Blink account.</summary>
        Custodial,
        /// <summary>Username does not resolve: a non-custodial (Spark) account served only via LNURL.</summary>
        NonCustodial
    }

    public record AccountInfo(AccountKind Kind, string? WalletId, string? WalletCurrency);

    // Cache resolution per (endpoint,username) so we don't re-query on every invoice/poll. Account
    // type is effectively immutable for a given username within a BTCPay run.
    private static readonly ConcurrentDictionary<string, AccountInfo> _accountCache = new();

    /// <summary>Derives the public GraphQL endpoint for a Blink lightning-address domain. blink.sv uses
    /// api.blink.sv; a bare "api." prefix is added for other domains; localhost keeps http.</summary>
    internal static Uri GraphQLEndpointForDomain(string domain)
    {
        // Strip any port for the host check, but preserve it when rebuilding.
        var host = domain;
        var isLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                      host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase);
        var scheme = isLocal ? "http" : "https";

        if (host.Equals("blink.sv", StringComparison.OrdinalIgnoreCase))
            return new Uri("https://api.blink.sv/graphql");

        // For self-hosted Galoy instances the API is conventionally on the "api." subdomain of the
        // lightning-address domain; if it already starts with api., keep it as-is.
        var apiHost = host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? host : $"api.{host}";
        return new Uri($"{scheme}://{apiHost}/graphql");
    }

    private static async Task<JObject> PostAsync(HttpClient httpClient, Uri endpoint, string query,
        object variables, CancellationToken cancellation)
    {
        var payload = new JObject
        {
            ["query"] = query,
            ["variables"] = JToken.FromObject(variables)
        };
        using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        using var resp = await httpClient.PostAsync(endpoint, content, cancellation);
        var body = await resp.Content.ReadAsStringAsync(cancellation);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Blink GraphQL request failed (HTTP {(int)resp.StatusCode}).");
        return JObject.Parse(body);
    }

    /// <summary>Resolves whether a username is custodial (default wallet resolves) or non-custodial,
    /// caching the result. A GraphQL error of "does not exist" means non-custodial; a transport error
    /// is rethrown so the caller can decide (we do NOT cache failures).</summary>
    public static async Task<AccountInfo> ResolveAccountAsync(HttpClient httpClient, Uri endpoint,
        string username, bool usd, CancellationToken cancellation)
    {
        var cacheKey = $"{endpoint}|{username}|{(usd ? "USD" : "BTC")}";
        if (_accountCache.TryGetValue(cacheKey, out var cached))
            return cached;

        const string query = @"query AccountDefaultWallet($username: Username!, $walletCurrency: WalletCurrency) {
  accountDefaultWallet(username: $username, walletCurrency: $walletCurrency) { id walletCurrency }
}";
        var resp = await PostAsync(httpClient, endpoint, query,
            new { username, walletCurrency = usd ? "USD" : "BTC" }, cancellation);

        var (info, confident) = ParseAccountInfo(resp);
        // Only cache a confident verdict: a resolved wallet (custodial) or an explicit "account does
        // not exist" (non-custodial). An ambiguous data:null with some other error (rate-limit,
        // timeout surfaced as 200, transient server error) must NOT be cached as NonCustodial - doing
        // so would pin a valid custodial account to the LNURL fallback for the whole plugin lifetime.
        // In that case we return NonCustodial for THIS call (safe fallback) but re-resolve next time.
        if (confident)
            _accountCache[cacheKey] = info;
        return info;
    }

    /// <summary>Parses an <c>accountDefaultWallet</c> response into an <see cref="AccountInfo"/> plus a
    /// <c>Confident</c> flag indicating whether the verdict is safe to cache.
    /// <list type="bullet">
    ///   <item>A resolved wallet id =&gt; Custodial, confident.</item>
    ///   <item>An explicit "does not exist" error =&gt; NonCustodial, confident (Spark username).</item>
    ///   <item>Any other <c>data:null</c>/error (transient/ambiguous) =&gt; NonCustodial, NOT confident,
    ///   so the caller can fall back for this call without caching a possibly-wrong verdict.</item>
    /// </list>
    /// Note the safe <c>as JObject</c> casts: for a non-custodial username <c>resp["data"]</c> is a
    /// JSON-null <see cref="JValue"/>, not C# null, so <c>?.["..."]</c> would throw "Cannot access child
    /// value on JValue" - the cast yields null instead.</summary>
    internal static (AccountInfo Info, bool Confident) ParseAccountInfo(JObject resp)
    {
        var wallet = (resp["data"] as JObject)?["accountDefaultWallet"] as JObject;
        if (wallet?["id"]?.Value<string>() is { Length: > 0 } walletId)
            return (new AccountInfo(AccountKind.Custodial, walletId, wallet["walletCurrency"]?.Value<string>()), true);

        var nonCustodial = new AccountInfo(AccountKind.NonCustodial, null, null);
        return (nonCustodial, IsAccountDoesNotExist(resp));
    }

    /// <summary>True when the response's errors clearly indicate the username has no Blink account
    /// (a Spark/non-custodial address), as opposed to a transient/ambiguous failure.</summary>
    private static bool IsAccountDoesNotExist(JObject resp)
    {
        if (resp["errors"] is not JArray errors)
            return false;
        foreach (var err in errors)
        {
            var message = (err as JObject)?["message"]?.Value<string>();
            if (message is not null &&
                message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Creates a fixed-amount invoice on behalf of a recipient wallet, committing to the given
    /// description hash (32-byte hex) so the BOLT11 h-tag matches BTCPay's served LNURL metadata.</summary>
    public static async Task<(string PaymentRequest, string PaymentHash)> CreateInvoiceOnBehalfAsync(
        HttpClient httpClient, Uri endpoint, string recipientWalletId, long amountSat,
        string? descriptionHashHex, string? memo, int expiresInMinutes, bool usd,
        CancellationToken cancellation)
    {
        var mutationName = usd
            ? "lnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipient"
            : "lnInvoiceCreateOnBehalfOfRecipient";
        var inputType = usd
            ? "LnUsdInvoiceBtcDenominatedCreateOnBehalfOfRecipientInput"
            : "LnInvoiceCreateOnBehalfOfRecipientInput";

        var query = $@"mutation CreateInvoice($input: {inputType}!) {{
  {mutationName}(input: $input) {{
    invoice {{ paymentHash paymentRequest }}
    errors {{ message }}
  }}
}}";

        object input = descriptionHashHex is { Length: > 0 }
            ? new { recipientWalletId, amount = amountSat, descriptionHash = descriptionHashHex, expiresIn = expiresInMinutes }
            : new { recipientWalletId, amount = amountSat, memo, expiresIn = expiresInMinutes };
        var resp = await PostAsync(httpClient, endpoint, query,
            new { input }, cancellation);
        return ParseInvoiceResult(resp, mutationName);
    }

    /// <summary>Polls the settlement status of an invoice by payment hash. Returns one of
    /// "PENDING", "PAID", "EXPIRED", or null when the invoice is unknown / on error.</summary>
    public static async Task<string?> GetPaymentStatusByHashAsync(HttpClient httpClient, Uri endpoint,
        string paymentHash, CancellationToken cancellation)
    {
        const string query = @"query PaymentStatus($input: LnInvoicePaymentStatusByHashInput!) {
  lnInvoicePaymentStatusByHash(input: $input) { status }
}";
        var resp = await PostAsync(httpClient, endpoint, query,
            new { input = new { paymentHash } }, cancellation);
        return ParseStatus(resp);
    }

    /// <summary>Parses an <c>lnInvoicePaymentStatusByHash</c> response, returning the status string or
    /// null when the invoice is unknown (<c>data</c> null / errors present). Uses <c>as JObject</c> to
    /// avoid the JSON-null <see cref="JValue"/> indexing exception.</summary>
    internal static string? ParseStatus(JObject resp)
        => ((resp["data"] as JObject)?["lnInvoicePaymentStatusByHash"] as JObject)?["status"]?.Value<string>();

    /// <summary>Parses an invoice-create mutation response. Returns the (paymentRequest, paymentHash)
    /// on success; otherwise throws with the GraphQL error message. Handles both a null <c>data</c>
    /// (top-level error) and per-field <c>errors</c>, using <c>as JObject</c> to stay null-safe.</summary>
    internal static (string PaymentRequest, string PaymentHash) ParseInvoiceResult(JObject resp, string mutationName)
    {
        var payload = (resp["data"] as JObject)?[mutationName] as JObject;
        var invoice = payload?["invoice"] as JObject;
        if (invoice?["paymentRequest"]?.Value<string>() is { Length: > 0 } pr)
            return (pr, invoice["paymentHash"]?.Value<string>() ?? "");

        // Prefer the mutation's own errors[]; fall back to the top-level errors[] (present when data is null).
        var errors = (payload?["errors"] as JArray) ?? (resp["errors"] as JArray);
        var message = errors is { Count: > 0 } ? (errors[0] as JObject)?["message"]?.Value<string>() : null;
        throw new Exception(message ?? "Blink did not return an invoice.");
    }
}
