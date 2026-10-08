#nullable enable
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>A CAIP-19 asset ID. Its CAIP-2 prefix is the network.</summary>
public sealed record CaipAsset(string Namespace, string ChainReference, string AssetNamespace, string AssetReference)
{
    private static readonly Regex Shape = new(@"\A([-a-z0-9]{3,8}):([-_a-zA-Z0-9]{1,32})/([-a-z0-9]{3,8}):([-.%a-zA-Z0-9]{1,128})\z",
        RegexOptions.CultureInvariant);

    public string ChainId => Namespace + ":" + ChainReference;

    public static CaipAsset? Parse(string? value)
    {
        if (value is null) return null;
        var m = Shape.Match(value);
        return m.Success ? new CaipAsset(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value) : null;
    }

    public override string ToString() => $"{ChainId}/{AssetNamespace}:{AssetReference}";
}
