#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

public sealed record RailDestination(string OptionId, string Destination, string? PaymentUri, string Verify, string? VerifyBatch, long AmountMsat);

/// <summary>Asks an LNURL for one rail's destination and refuses any answer whose settlement could not be learnt.</summary>
public static class LnurlRailRequester
{
    public static async Task<RailDestination> Request(HttpClient http, Uri payEndpoint, LnurlRail rail, long amountMsat,
        DateTimeOffset invoiceExpiry, Network network, CancellationToken ct)
    {
        var pay = await Get(http, payEndpoint, "the LNURL could not be read", ct);
        var option = PaymentOption.Parse(pay).FirstOrDefault(o => o.Type.Equals(rail.OptionType, StringComparison.OrdinalIgnoreCase))
                     ?? throw Unavailable($"the LNURL does not offer '{rail.OptionType}'");
        if (!option.Available) throw Unavailable($"the LNURL reports '{rail.OptionType}' as currently unavailable");
        var min = option.MinSendable ?? pay["minSendable"]?.Value<long>() ?? 1;
        var max = option.MaxSendable ?? pay["maxSendable"]?.Value<long>() ?? long.MaxValue;
        if (amountMsat < min || amountMsat > max)
            throw Unavailable($"{amountMsat} msat is outside the '{option.Id}' bounds ({min}-{max} msat)");
        var callback = Str(pay["callback"]) ?? throw Unavailable("the LNURL has no callback");

        var json = await Get(http, LNURLReceiver.CallbackUri(callback, amountMsat, option.Id, null), "the LNURL refused the request", ct);
        if (Str(json["paymentOption"]) is { } echoed && echoed != option.Id)
            throw Unavailable($"the LNURL answered for '{echoed}' instead of '{option.Id}'");
        var destination = Str(json["paymentDestination"]) ?? throw Unavailable("the LNURL returned no destination");
        var verify = Str(json["verify"]);
        if (verify is null || !Uri.TryCreate(verify, UriKind.Absolute, out var verifyUri) || (verifyUri.Scheme != Uri.UriSchemeHttp && verifyUri.Scheme != Uri.UriSchemeHttps))
            throw Unavailable("settlement cannot be detected: the LNURL returned no usable verify URL");
        if (json["expiresAt"]?.Type == JTokenType.Integer &&
            DateTimeOffset.FromUnixTimeSeconds(json["expiresAt"]!.Value<long>()) < invoiceExpiry)
            throw Unavailable("the destination expires before the invoice");
        if (!rail.IsValidDestination(destination, network))
            throw Unavailable($"'{destination}' is not a valid {rail.Label} destination for {network.ChainName}");
        return new RailDestination(option.Id, destination, Str(json["paymentURI"]), verify,
            LNURLReceiver.BatchUrl(json["verifyBatch"], verify), amountMsat);
    }

    private static async Task<JObject> Get(HttpClient http, Uri uri, string what, CancellationToken ct)
    {
        try { return await LNURLResolver.GetJson(http, uri, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { throw Unavailable($"{what} ({e.Message})"); }
    }

    private static PaymentMethodUnavailableException Unavailable(string reason) => new(reason);

    private static string? Str(JToken? t) => t?.Type == JTokenType.String && t.Value<string>() is { Length: > 0 } s ? s : null;
}
