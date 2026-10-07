#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>One LUD-XX <c>paymentOptions</c> entry advertised by a payRequest.</summary>
/// <param name="Verifiable">Whether callback answers carry a LUD-21 verify URL; null when the service does not say.</param>
/// <param name="Asset">A CAIP-19 asset ID, on options for a token on a non-Bitcoin network.</param>
public sealed record PaymentOption(string Id, string Type, bool Available, long? MinSendable, long? MaxSendable, bool? Verifiable = null,
    string? Asset = null, string? Unit = null)
{
    public static IReadOnlyList<PaymentOption> Parse(JObject payRequest)
    {
        var options = new List<PaymentOption>();
        if (payRequest["paymentOptions"] is not JArray entries) return options;
        foreach (var entry in entries)
        {
            if (entry is not JObject o || Str(o["id"]) is not { Length: > 0 } id || Str(o["type"]) is not { Length: > 0 } type)
                continue;
            options.Add(new PaymentOption(id, type,
                o["available"]?.Type != JTokenType.Boolean || o["available"]!.Value<bool>(),
                Msat(o["minSendable"]), Msat(o["maxSendable"]),
                o["verifiable"]?.Type == JTokenType.Boolean ? o["verifiable"]!.Value<bool>() : null,
                Str(o["asset"]), Str(o["unit"])));
        }
        return options;
    }

    /// <summary>
    /// The callback's <c>paymentOption</c> value (null leaves the request plain LUD-06) and the bounds a
    /// Lightning amount must satisfy: the lightning option's own, each falling back to the top level.
    /// </summary>
    public static (long Min, long Max, string? OptionId) PlanLightning(JObject payRequest, long defaultMin, long defaultMax)
    {
        var min = payRequest["minSendable"]?.Value<long>() ?? defaultMin;
        var max = payRequest["maxSendable"]?.Value<long>() ?? defaultMax;
        var lightning = Parse(payRequest).Where(o => o.Type.Equals("lightning", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lightning.Count == 0) return (min, max, null);
        var option = lightning.FirstOrDefault(o => o.Available)
                     ?? throw new NotSupportedException("The LNURL service reports Lightning as currently unavailable.");
        return (option.MinSendable ?? min, option.MaxSendable ?? max, option.Id);
    }

    private static string? Str(JToken? t) => t?.Type == JTokenType.String ? t.Value<string>() : null;

    // Every option is parsed to plan Lightning, so a bound no msat amount can hold is ignored rather than thrown on.
    private static long? Msat(JToken? t) => t switch
    {
        JValue { Type: JTokenType.Integer, Value: long v } when v >= 0 => v,
        JValue { Type: JTokenType.Float } f when f.Value<double>() is >= 0 and < 9.2e18 => (long)f.Value<double>(),
        _ => null
    };
}
