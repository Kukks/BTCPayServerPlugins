#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
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
        long Min(PaymentOption o) => o.MinSendable ?? pay["minSendable"]?.Value<long>() ?? 1;
        long Max(PaymentOption o) => o.MaxSendable ?? pay["maxSendable"]?.Value<long>() ?? long.MaxValue;
        // Ids are unique but types are not: of the options declared verifiable, then those that do not say, take one that can
        // be paid this amount now, so no sibling gets the rail refused (or remembered as unverifiable) in its place.
        var verifiable = offered.Where(o => o.Verifiable == true).Concat(offered.Where(o => o.Verifiable is null)).ToList();
        var pool = verifiable.Count > 0 ? verifiable : offered;
        var option = pool.FirstOrDefault(o => o.Available && amountMsat >= Min(o) && amountMsat <= Max(o)) ?? pool.FirstOrDefault(o => o.Available)
                     ?? throw Unavailable($"the LNURL reports '{rail.OptionType}' as currently unavailable");
        if (option.Verifiable == false)
            throw new UnverifiableRailException($"settlement cannot be detected: the LNURL marks '{rail.OptionType}' as not verifiable");
        var (min, max) = (Min(option), Max(option));
        if (amountMsat < min || amountMsat > max)
            throw Unavailable($"{amountMsat} msat is outside the '{option.Id}' bounds ({min}-{max} msat)");
        var callback = Str(pay["callback"]) ?? throw Unavailable("the LNURL has no callback");

        var callbackUri = LNURLReceiver.CallbackUri(callback, amountMsat, option.Id, null);
        var json = await Get(http, callbackUri, "the LNURL refused the request", ct);
        if (Str(json["paymentOption"]) is { } echoed && echoed != option.Id)
            throw Unavailable($"the LNURL answered for '{echoed}' instead of '{option.Id}'");
        var destination = Str(json["paymentDestination"]) ?? throw Unavailable("the LNURL returned no destination");
        var (verify, batch) = Settlement(json, callbackUri, invoiceExpiry);
        if (!rail.IsValidDestination(destination, network))
            throw Unavailable($"'{destination}' is not a valid {rail.Label} destination for {network.ChainName}");
        return new RailDestination(option.Id, destination, Str(json["paymentURI"]), verify, batch, amountMsat);
    }

    /// <summary>The verify and verifyBatch URLs of a callback answer, refusing one whose settlement could not be learnt.</summary>
    internal static (string Verify, string? VerifyBatch) Settlement(JObject answer, Uri callbackUri, DateTimeOffset invoiceExpiry)
    {
        var verify = Str(answer["verify"]);
        if (verify is null || !Uri.TryCreate(verify, UriKind.Absolute, out var verifyUri) || (verifyUri.Scheme != Uri.UriSchemeHttp && verifyUri.Scheme != Uri.UriSchemeHttps))
            throw new UnverifiableRailException("settlement cannot be detected: the LNURL returned no usable verify URL");
        // Rail settlement rests on verify alone, so an on-path attacker answering it could forge a paid invoice.
        if (verifyUri.Scheme == Uri.UriSchemeHttp && callbackUri.Scheme == Uri.UriSchemeHttps)
            throw new UnverifiableRailException("settlement cannot be detected: the LNURL returned a plain-http verify URL for an https callback");
        if (ExpiresAt(answer["expiresAt"]) is { } at && DateTimeOffset.FromUnixTimeSeconds(at) < invoiceExpiry)
            throw Unavailable("the destination expires before the invoice");
        return (verify, LNURLReceiver.BatchUrl(answer["verifyBatch"], verify));
    }

    private const long MaxUnixSeconds = 253_402_300_799;

    // A stated expiry that cannot be read, or that DateTimeOffset cannot hold, refuses the rail rather than go unchecked.
    internal static long? ExpiresAt(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null) return null;
        var seconds = UnixSeconds(token);
        if (seconds is null or < 0 or > MaxUnixSeconds) throw Unavailable("the LNURL returned an expiry that could not be read");
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

    internal static async Task<JObject> Get(HttpClient http, Uri uri, string what, CancellationToken ct)
    {
        try { return await LNURLResolver.GetJson(http, uri, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { throw Unavailable($"{what} ({e.Message})"); }
    }

    private static PaymentMethodUnavailableException Unavailable(string reason) => new(reason);

    internal static string? Str(JToken? t) => t?.Type == JTokenType.String && t.Value<string>() is { Length: > 0 } s ? s : null;
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
