#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>One LUD-XX <c>paymentOptions</c> entry advertised by a payRequest.</summary>
/// <param name="Verifiable">Whether callback answers carry a LUD-21 verify URL; null when the service does not say.</param>
public sealed record PaymentOption(string Id, string Type, bool Available, long? MinSendable, long? MaxSendable, bool? Verifiable = null)
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
                o["verifiable"]?.Type == JTokenType.Boolean ? o["verifiable"]!.Value<bool>() : null));
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
        foreach (var o in Parse(payRequest))
        {
            if (!o.Type.Equals("lightning", StringComparison.OrdinalIgnoreCase)) continue;
            if (!o.Available)
                throw new NotSupportedException("The LNURL service reports Lightning as currently unavailable.");
            return (o.MinSendable ?? min, o.MaxSendable ?? max, o.Id);
        }
        return (min, max, null);
    }

    private static string? Str(JToken? t) => t?.Type == JTokenType.String ? t.Value<string>() : null;

    private static long? Msat(JToken? t) =>
        t?.Type is JTokenType.Integer or JTokenType.Float ? t.Value<long>() : null;
}
