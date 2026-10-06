#nullable enable
using System;
using System.Collections.Concurrent;
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
        var offered = PaymentOption.Parse(pay).Where(o => o.Type.Equals(rail.OptionType, StringComparison.OrdinalIgnoreCase)).ToList();
        if (offered.Count == 0) throw Unavailable($"the LNURL does not offer '{rail.OptionType}'");
        // Ids are unique but types are not: try options declared verifiable, then ones that do not say, so no sibling gets the rail refused for a day.
        var verifiable = offered.Where(o => o.Verifiable == true).Concat(offered.Where(o => o.Verifiable is null)).ToList();
        var option = (verifiable.Count > 0 ? verifiable : offered).FirstOrDefault(o => o.Available)
                     ?? throw Unavailable($"the LNURL reports '{rail.OptionType}' as currently unavailable");
        if (option.Verifiable == false)
            throw new UnverifiableRailException($"settlement cannot be detected: the LNURL marks '{rail.OptionType}' as not verifiable");
        var min = option.MinSendable ?? pay["minSendable"]?.Value<long>() ?? 1;
        var max = option.MaxSendable ?? pay["maxSendable"]?.Value<long>() ?? long.MaxValue;
        if (amountMsat < min || amountMsat > max)
            throw Unavailable($"{amountMsat} msat is outside the '{option.Id}' bounds ({min}-{max} msat)");
        var callback = Str(pay["callback"]) ?? throw Unavailable("the LNURL has no callback");

        var callbackUri = LNURLReceiver.CallbackUri(callback, amountMsat, option.Id, null);
        var json = await Get(http, callbackUri, "the LNURL refused the request", ct);
        if (Str(json["paymentOption"]) is { } echoed && echoed != option.Id)
            throw Unavailable($"the LNURL answered for '{echoed}' instead of '{option.Id}'");
        var destination = Str(json["paymentDestination"]) ?? throw Unavailable("the LNURL returned no destination");
        var verify = Str(json["verify"]);
        if (verify is null || !Uri.TryCreate(verify, UriKind.Absolute, out var verifyUri) || (verifyUri.Scheme != Uri.UriSchemeHttp && verifyUri.Scheme != Uri.UriSchemeHttps))
            throw new UnverifiableRailException("settlement cannot be detected: the LNURL returned no usable verify URL");
        // Rail settlement rests on verify alone, so an on-path attacker answering it could forge a paid invoice.
        if (verifyUri.Scheme == Uri.UriSchemeHttp && callbackUri.Scheme == Uri.UriSchemeHttps)
            throw new UnverifiableRailException("settlement cannot be detected: the LNURL returned a plain-http verify URL for an https callback");
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

/// <summary>A refusal that will repeat for this LNURL and rail, because its settlement cannot be learnt.</summary>
public sealed class UnverifiableRailException : PaymentMethodUnavailableException
{
    public UnverifiableRailException(string message) : base(message) { }
}

/// <summary>LNURL rails refused as unverifiable, kept off new invoices for a day.</summary>
public static class UnverifiableRails
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(1);
    private static readonly ConcurrentDictionary<(string Lnurl, string OptionType), DateTimeOffset> _until = new();

    public static void Remember(string lnurl, string optionType, DateTimeOffset now)
    {
        foreach (var kv in _until)
            if (kv.Value <= now) _until.TryRemove(kv.Key, out _);
        _until[(lnurl, optionType)] = now + Window;
    }

    public static bool Knows(string lnurl, string optionType, DateTimeOffset now) =>
        _until.TryGetValue((lnurl, optionType), out var until) && until > now;
}
