#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

public enum LnurlCapability { ReceiveOnly, SendAndReceive }

/// <summary>
/// The outcome of decoding a connection-string value: what capability it grants and the endpoints
/// needed to act on it. <see cref="PayEndpoint"/> is the LNURL-pay metadata endpoint used for
/// receiving (either the value itself, or the payLink embedded in an LNURL-withdraw).
/// </summary>
public sealed record ResolvedLnurl(
    LnurlCapability Capability,
    Uri PayEndpoint,
    LNURL.LNURLWithdrawRequest? Withdraw,
    string DisplayHost);

public static class LNURLResolver
{
    /// <summary>
    /// Decodes an LN address / LNURL / raw https endpoint and branches on its tag:
    /// payRequest (or LN address) -> receive-only; withdrawRequest with a payLink -> send+receive;
    /// withdrawRequest without a (valid, payRequest) payLink -> FormatException.
    /// </summary>
    public static async Task<ResolvedLnurl> Resolve(string value, Network network, HttpClient http, CancellationToken ct)
    {
        value = value.Trim();

        Uri endpoint;
        if (value.Contains('@'))
            endpoint = LNURL.LNURL.ExtractUriFromInternetIdentifier(value);
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            endpoint = new Uri(value);
        else
            endpoint = LNURL.LNURL.Parse(value, out _);

        var root = await GetJson(http, endpoint, ct);
        var tag = root["tag"]?.Value<string>();

        // LN addresses always resolve to a payRequest; treat an explicit payRequest tag the same.
        if (value.Contains('@') || tag == "payRequest")
            return new ResolvedLnurl(LnurlCapability.ReceiveOnly, endpoint, null, endpoint.Host);

        if (tag == "withdrawRequest")
        {
            var withdraw = root.ToObject<LNURL.LNURLWithdrawRequest>()
                           ?? throw new FormatException("Could not parse the LNURL-withdraw response.");
            var payLink = root["payLink"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(payLink) || !Uri.TryCreate(payLink, UriKind.Absolute, out var payUri))
                throw new FormatException(
                    "This LNURL-withdraw has no payLink, so it cannot receive. A receive-capable backend is required — " +
                    "provide an LNURL-withdraw whose response includes a payLink, or an LNURL-pay / Lightning address for receive-only.");

            // Fail fast at config time: the payLink must itself be a valid LNURL-pay endpoint.
            var payJson = await GetJson(http, payUri, ct);
            if (payJson["tag"]?.Value<string>() != "payRequest")
                throw new FormatException("The LNURL-withdraw's payLink is not a valid LNURL-pay endpoint.");

            return new ResolvedLnurl(LnurlCapability.SendAndReceive, payUri, withdraw, endpoint.Host);
        }

        throw new FormatException($"Unsupported LNURL tag '{tag}'. Expected 'payRequest' or 'withdrawRequest'.");
    }

    internal static async Task<JObject> GetJson(HttpClient http, Uri uri, CancellationToken ct)
    {
        using var resp = await http.GetAsync(uri, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new FormatException($"LNURL endpoint '{uri}' returned HTTP {(int)resp.StatusCode}.");
        var json = JObject.Parse(body);
        if (json["status"]?.Value<string>()?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true)
            throw new FormatException(json["reason"]?.Value<string>() ?? $"LNURL endpoint '{uri}' returned an error.");
        return json;
    }
}
