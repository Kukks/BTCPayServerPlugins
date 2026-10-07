#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>A unit from the payRequest's <c>units</c> (LUD-XX payment quote).</summary>
public sealed record TokenUnit(string Code, int Decimals, string? Name);

/// <summary>A payment option for a token on a non-Bitcoin network; its <c>type</c> is its asset's CAIP-2 namespace.</summary>
public sealed record TokenOption(string Id, CaipAsset Asset, TokenUnit Unit, bool Available, long? MinSendable, long? MaxSendable,
    bool? Verifiable)
{
    private static readonly Regex Code = new(@"\A[A-Za-z0-9]{1,16}\z", RegexOptions.CultureInvariant);

    public static IReadOnlyList<TokenOption> Parse(JObject payRequest)
    {
        var units = Units(payRequest);
        var offered = PaymentOption.Parse(payRequest);
        // The handler keys its state by id and the callback asks by id, so two options under one id would pair
        // one network's token with the other's quote and verify URL.
        var shared = offered.GroupBy(o => o.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var options = new List<TokenOption>();
        foreach (var o in offered)
        {
            if (shared.Contains(o.Id) || CaipAsset.Parse(o.Asset) is not { } asset || asset.Namespace != o.Type ||
                TokenNamespaces.For(asset) is null || o.Unit is null || !units.TryGetValue(o.Unit, out var unit))
                continue;
            options.Add(new TokenOption(o.Id, asset, unit, o.Available, o.MinSendable, o.MaxSendable, o.Verifiable));
        }
        return options;
    }

    public static IReadOnlyDictionary<string, TokenUnit> Units(JObject payRequest)
    {
        var units = new Dictionary<string, TokenUnit>(StringComparer.OrdinalIgnoreCase);
        if (payRequest["units"] is not JArray entries) return units;
        foreach (var e in entries.OfType<JObject>())
        {
            var code = e["code"]?.Type == JTokenType.String ? e["code"]!.Value<string>() : null;
            var decimals = e["decimals"] is JValue { Type: JTokenType.Integer, Value: long d } ? d : -1;
            if (code is null || !Code.IsMatch(code) || decimals is < 0 or > 36) continue;
            var name = e["name"]?.Type == JTokenType.String ? e["name"]!.Value<string>() : null;
            units.TryAdd(code, new TokenUnit(code.ToUpperInvariant(), (int)decimals, name is { Length: > 0 and <= 64 } ? name : null));
        }
        return units;
    }
}
