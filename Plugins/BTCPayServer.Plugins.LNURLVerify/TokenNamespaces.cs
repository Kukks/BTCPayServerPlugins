#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="Uri">The QR and deeplink URI for (asset, recipient, base units, decimals); null when the namespace has none.</param>
public sealed record TokenNamespace(string Name, string AssetNamespace, Func<string, bool> IsValidChain,
    Func<string, bool> IsValidAddress, Func<CaipAsset, string, string, int, string?> Uri);

/// <summary>The only per-chain code: one row per supported CAIP-2 namespace.</summary>
public static class TokenNamespaces
{
    private static readonly Regex EvmAddress = new(@"\A0x[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ChainNumber = new(@"\A[1-9][0-9]{0,18}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SolanaAddressShape = new(@"\A[1-9A-HJ-NP-Za-km-z]{32,44}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TronAddressShape = new(@"\AT[1-9A-HJ-NP-Za-km-z]{33}\z", RegexOptions.CultureInvariant);

    public static readonly IReadOnlyDictionary<string, TokenNamespace> All = new Dictionary<string, TokenNamespace>
    {
        ["eip155"] = new("eip155", "erc20", c => ChainNumber.IsMatch(c), a => EvmAddress.IsMatch(a),
            (asset, to, amount, _) => $"ethereum:{asset.AssetReference}@{asset.ChainReference}/transfer?address={to}&uint256={amount}"),
        ["solana"] = new("solana", "token", _ => true, IsSolanaAddress,
            (asset, to, amount, decimals) => $"solana:{to}?amount={TokenAmount.Format(amount, decimals)}&spl-token={asset.AssetReference}"),
        ["tron"] = new("tron", "trc20", _ => true, IsTronAddress, (_, _, _, _) => null)
    };

    /// <summary>The asset's namespace row, when the namespace is supported and the asset is a well-formed token of it.</summary>
    public static TokenNamespace? For(CaipAsset asset) =>
        All.TryGetValue(asset.Namespace, out var ns) && ns.AssetNamespace == asset.AssetNamespace &&
        ns.IsValidChain(asset.ChainReference) && ns.IsValidAddress(asset.AssetReference) ? ns : null;

    private static bool IsSolanaAddress(string value)
    {
        if (!SolanaAddressShape.IsMatch(value)) return false;
        try { return Encoders.Base58.DecodeData(value).Length == 32; }
        catch (Exception e) when (e is FormatException or ArgumentException) { return false; }
    }

    private static bool IsTronAddress(string value)
    {
        if (!TronAddressShape.IsMatch(value)) return false;
        try
        {
            var bytes = Encoders.Base58Check.DecodeData(value);
            return bytes.Length == 21 && bytes[0] == 0x41;
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { return false; }
    }
}
