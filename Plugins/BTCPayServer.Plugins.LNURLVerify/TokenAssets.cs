#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// The unit codes this server offers as checkout tabs. They come from configuration, not the database: payment methods
/// are registered at startup, before the database is reachable.
/// </summary>
public sealed class TokenAssets
{
    public const string ConfigKey = "LNURLVERIFY_ASSETS";
    public const string Default = "USDT,USDC";
    private static readonly Regex Code = new(@"\A[A-Z0-9]{1,16}\z", RegexOptions.CultureInvariant);

    public TokenAssets(IEnumerable<string> codes) => Codes = codes.ToArray();

    public IReadOnlyList<string> Codes { get; }
    public IEnumerable<PaymentMethodId> PaymentMethodIds => Codes.Select(PaymentMethodIdOf);

    public static PaymentMethodId PaymentMethodIdOf(string code) => new("LNURL-" + code);

    public static (TokenAssets Assets, IReadOnlyList<string> Rejected) Parse(string? value)
    {
        var codes = new List<string>();
        var rejected = new List<string>();
        foreach (var entry in (value ?? Default).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = entry.ToUpperInvariant();
            if (!Code.IsMatch(code) || LnurlRails.IsRail(PaymentMethodIdOf(code))) rejected.Add(entry);
            else if (!codes.Contains(code)) codes.Add(code);
        }
        return (new TokenAssets(codes), rejected);
    }
}
