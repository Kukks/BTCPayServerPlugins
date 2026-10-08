#nullable enable
using System;
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
        // available:false means down right now, not withdrawn, so the network stays on offer instead of being refused for the invoice.
        if (!option.Available) throw new TransientRailException($"the LNURL reports '{option.Id}' as currently unavailable");
        if (option.Verifiable == false)
            throw new UnverifiableRailException($"settlement cannot be detected: the LNURL marks '{option.Id}' as not verifiable");
        var min = option.MinSendable ?? payRequest["minSendable"]?.Value<long>() ?? 1;
        var max = option.MaxSendable ?? payRequest["maxSendable"]?.Value<long>() ?? long.MaxValue;
        if (amountMsat < min || amountMsat > max)
            throw Unavailable($"{amountMsat} msat is outside the '{option.Id}' bounds ({min}-{max} msat)");
        var callback = LnurlRailRequester.Str(payRequest["callback"]) ?? throw Unavailable("the LNURL has no callback");

        var callbackUri = LNURLReceiver.CallbackUri(callback, amountMsat, option.Id, null);
        var answer = await LnurlRailRequester.Get(http, callbackUri, "the LNURL request failed", ct);
        if (LnurlRailRequester.Str(answer["paymentOption"]) is { } echoed && echoed != option.Id)
            throw Unavailable($"the LNURL answered for '{echoed}' instead of '{option.Id}'");
        var destination = LnurlRailRequester.Str(answer["paymentDestination"]) ?? throw Unavailable("the LNURL returned no destination");
        var (verify, batch) = LnurlRailRequester.Settlement(answer, callbackUri, invoiceExpiry);
        if (!TokenNamespaces.For(option.Asset)!.IsValidAddress(destination))
            throw Unavailable($"the LNURL returned a destination that is not a valid {option.Asset.Namespace} address");
        if (answer["paymentDestinationTag"] is { Type: not JTokenType.Null } tag)
            throw Unavailable($"the LNURL requires a destination tag ('{tag}') for '{option.Id}', which the checkout cannot attach");
        var quote = answer["paymentQuote"] as JObject;
        var payment = quote?["payment"] as JObject;
        var amountToken = payment?["amount"];
        var amount = amountToken is { Type: JTokenType.String or JTokenType.Integer } ? amountToken.ToString() : null;
        if (!TokenAmount.IsBaseUnits(amount) ||
            !string.Equals(LnurlRailRequester.Str(payment!["unit"]), option.Unit.Code, StringComparison.OrdinalIgnoreCase))
            throw Unavailable($"the LNURL quoted no whole amount of {option.Unit.Code}");
        var expiresAt = LnurlRailRequester.ExpiresAt(quote!["expiresAt"]);
        if (expiresAt is { } at && at <= now.ToUnixTimeSeconds()) throw Unavailable("the quote has already expired");
        return new TokenQuote(option.Id, destination, amount!, expiresAt, verify, batch, amountMsat);
    }

    private static PaymentMethodUnavailableException Unavailable(string reason) => new(reason);
}
