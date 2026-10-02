#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="Amount">The quoted amount in the unit's base units.</param>
public sealed record TokenQuote(string OptionId, string Destination, string Amount, long? ExpiresAt, string Verify, string? VerifyBatch,
    long AmountMsat);

/// <summary>Asks an LNURL for one token network's destination and quote, refusing anything the checkout could not show or settle.</summary>
public static class LnurlTokenRequester
{
    public static async Task<TokenQuote> Request(HttpClient http, JObject payRequest, TokenOption option, long amountMsat,
        DateTimeOffset invoiceExpiry, DateTimeOffset now, CancellationToken ct)
    {
        if (!option.Available) throw Unavailable($"the LNURL reports '{option.Id}' as currently unavailable");
        if (option.Verifiable == false)
            throw new UnverifiableRailException($"settlement cannot be detected: the LNURL marks '{option.Id}' as not verifiable");
        var min = option.MinSendable ?? payRequest["minSendable"]?.Value<long>() ?? 1;
        var max = option.MaxSendable ?? payRequest["maxSendable"]?.Value<long>() ?? long.MaxValue;
        if (amountMsat < min || amountMsat > max)
            throw Unavailable($"{amountMsat} msat is outside the '{option.Id}' bounds ({min}-{max} msat)");
        var callback = LnurlRailRequester.Str(payRequest["callback"]) ?? throw Unavailable("the LNURL has no callback");

        var callbackUri = LNURLReceiver.CallbackUri(callback, amountMsat, option.Id, null);
        var answer = await LnurlRailRequester.Get(http, callbackUri, "the LNURL refused the request", ct);
        if (LnurlRailRequester.Str(answer["paymentOption"]) is { } echoed && echoed != option.Id)
            throw Unavailable($"the LNURL answered for '{echoed}' instead of '{option.Id}'");
        var destination = LnurlRailRequester.Str(answer["paymentDestination"]) ?? throw Unavailable("the LNURL returned no destination");
        var (verify, batch) = LnurlRailRequester.Settlement(answer, callbackUri, invoiceExpiry);
        if (!TokenNamespaces.For(option.Asset)!.IsValidAddress(destination))
            throw Unavailable($"the LNURL returned a destination that is not a valid {option.Asset.Namespace} address");
        var quote = answer["paymentQuote"] as JObject;
        var payment = quote?["payment"] as JObject;
        var amountToken = payment?["amount"];
        var amount = amountToken is { Type: JTokenType.String or JTokenType.Integer } ? amountToken.ToString() : null;
        if (!TokenAmount.IsBaseUnits(amount) ||
            !string.Equals(LnurlRailRequester.Str(payment!["unit"]), option.Unit.Code, StringComparison.OrdinalIgnoreCase))
            throw Unavailable($"the LNURL quoted no whole amount of {option.Unit.Code}");
        var expiresAt = ExpiresAt(quote!["expiresAt"]);
        if (expiresAt is { } at && at <= now.ToUnixTimeSeconds()) throw Unavailable("the quote has already expired");
        return new TokenQuote(option.Id, destination, amount!, expiresAt, verify, batch, amountMsat);
    }

    private const long MaxUnixSeconds = 253_402_300_799;

    // A stated expiry that cannot be read, or that DateTimeOffset cannot hold, refuses the network rather than show its quote forever.
    private static long? ExpiresAt(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null) return null;
        var seconds = UnixSeconds(token);
        if (seconds is null or < 0 or > MaxUnixSeconds) throw Unavailable("the LNURL quoted an expiry that could not be read");
        return seconds;
    }

    private static long? UnixSeconds(JToken token)
    {
        if (token is JValue { Type: JTokenType.Integer, Value: long n }) return n;
        // Json.NET has already read an ISO 8601 string as a date; one without a zone is UTC, as below.
        if (token.Type == JTokenType.Date)
        {
            var date = token.Value<DateTime>();
            return new DateTimeOffset(date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime()).ToUnixTimeSeconds();
        }
        var s = token.Type == JTokenType.String ? token.Value<string>() : null;
        if (s is { Length: > 0 and <= 12 } && s.All(char.IsAsciiDigit)) return long.Parse(s, CultureInfo.InvariantCulture);
        if (s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return at.ToUnixTimeSeconds();
        return null;
    }

    private static PaymentMethodUnavailableException Unavailable(string reason) => new(reason);
}
