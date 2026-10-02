#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="UriParam">"lightning", a rail's BIP321 key, or null when the destination is the URI path.</param>
public sealed record RailState(PaymentMethodId PaymentMethodId, string Label, bool Active, string? Destination, string? Uri, string? UriParam);

public static class Bip321
{
    public static string Amount(decimal btc) => btc.ToString("0.########", CultureInfo.InvariantCulture);

    public static string Build(string? address, decimal? amount, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var all = amount is { } a
            ? new[] { new KeyValuePair<string, string>("amount", Amount(a)) }.Concat(parameters)
            : parameters;
        return Extend("bitcoin:" + address, all);
    }

    public static string Extend(string uri, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var present = new HashSet<string>(Keys(uri), StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(uri);
        var hasQuery = uri.Contains('?');
        foreach (var p in parameters)
        {
            if (!present.Add(p.Key)) continue;
            sb.Append(hasQuery ? '&' : '?').Append(p.Key).Append('=').Append(System.Uri.EscapeDataString(p.Value));
            hasQuery = true;
        }
        return sb.ToString();
    }

    /// <summary>Uppercases a bech32 address for a denser QR; parameter values may be case-sensitive, so they stay verbatim.</summary>
    public static string Qr(string uri)
    {
        if (uri.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            return "lightning:" + uri.Substring("lightning:".Length).ToUpperInvariant();
        if (!uri.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase)) return uri;
        var q = uri.IndexOf('?');
        var path = q < 0 ? uri.Substring(8) : uri.Substring(8, q - 8);
        var bech32 = new[] { "bc1", "tb1", "bcrt1" }.Any(h => path.StartsWith(h, StringComparison.OrdinalIgnoreCase));
        return "bitcoin:" + (bech32 ? path.ToUpperInvariant() : path) + (q < 0 ? "" : uri.Substring(q));
    }

    /// <summary>One URI for every active rail: an active on-chain rail's link is the base, a lone rail keeps its own.</summary>
    public static string? Merge(IReadOnlyList<RailState> rails, decimal? amount)
    {
        var active = rails.Where(r => r.Active && r.Uri is not null && r.Destination is not null).ToList();
        if (active.Count == 0) return null;
        if (active.Count == 1) return active[0].Uri;
        var onchain = active.FirstOrDefault(r => r.UriParam is null);
        var parameters = active.Where(r => r.UriParam is not null)
            .Select(r => new KeyValuePair<string, string>(r.UriParam!, r.Destination!));
        return onchain is not null ? Extend(onchain.Uri!, parameters) : Build(null, amount, parameters);
    }

    private static IEnumerable<string> Keys(string uri)
    {
        var q = uri.IndexOf('?');
        if (q < 0) yield break;
        foreach (var part in uri.Substring(q + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
            yield return part.Split('=')[0];
    }
}
