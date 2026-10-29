#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Network names, icons and explorers, resolved locally from CAIP-2 so the LNURL never has to send them.</summary>
public static class ChainDirectory
{
    // Icon: a file in Resources/lnurlverify/icons; a testnet shares its mainnet's.
    private static readonly Dictionary<string, (string Label, string Icon, string Tx)> Chains = new()
    {
        ["eip155:1"] = ("Ethereum", "ethereum", "https://etherscan.io/tx/{0}"),
        ["eip155:11155111"] = ("Sepolia", "ethereum", "https://sepolia.etherscan.io/tx/{0}"),
        ["eip155:42161"] = ("Arbitrum One", "arbitrum", "https://arbiscan.io/tx/{0}"),
        ["eip155:421614"] = ("Arbitrum Sepolia", "arbitrum", "https://sepolia.arbiscan.io/tx/{0}"),
        ["eip155:8453"] = ("Base", "base", "https://basescan.org/tx/{0}"),
        ["eip155:84532"] = ("Base Sepolia", "base", "https://sepolia.basescan.org/tx/{0}"),
        ["eip155:10"] = ("Optimism", "optimism", "https://optimistic.etherscan.io/tx/{0}"),
        ["eip155:11155420"] = ("OP Sepolia", "optimism", "https://sepolia-optimism.etherscan.io/tx/{0}"),
        ["eip155:137"] = ("Polygon", "polygon", "https://polygonscan.com/tx/{0}"),
        ["eip155:80002"] = ("Polygon Amoy", "polygon", "https://amoy.polygonscan.com/tx/{0}"),
        ["eip155:56"] = ("BNB Smart Chain", "bnb", "https://bscscan.com/tx/{0}"),
        ["eip155:43114"] = ("Avalanche", "avalanche", "https://snowtrace.io/tx/{0}"),
        ["solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp"] = ("Solana", "solana", "https://solscan.io/tx/{0}"),
        ["solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1"] = ("Solana Devnet", "solana", "https://solscan.io/tx/{0}?cluster=devnet"),
        ["solana:4uhcVJyU9pJkvQyS88uRDiswHXSCkY3z"] = ("Solana Testnet", "solana", "https://solscan.io/tx/{0}?cluster=testnet"),
        ["tron:0x2b6653dc"] = ("Tron", "tron", "https://tronscan.org/#/transaction/{0}"),
        ["tron:0xcd8690dc"] = ("Tron Nile", "tron", "https://nile.tronscan.org/#/transaction/{0}"),
        ["tron:0x94a9059e"] = ("Tron Shasta", "tron", "https://shasta.tronscan.org/#/transaction/{0}")
    };

    private static readonly HashSet<string> TokenIcons = new() { "USDT", "USDC" };

    private static readonly Dictionary<string, Regex> TxIds = new()
    {
        ["eip155"] = new(@"\A0x[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant),
        ["solana"] = new(@"\A[1-9A-HJ-NP-Za-km-z]{64,88}\z", RegexOptions.CultureInvariant),
        ["tron"] = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
    };

    public static string Label(string chainId) => Chains.TryGetValue(chainId, out var chain) ? chain.Label : chainId;

    public static string? Icon(string chainId) => Chains.TryGetValue(chainId, out var chain) ? chain.Icon : null;

    public static string? TokenIcon(string unitCode) => TokenIcons.Contains(unitCode) ? unitCode.ToLowerInvariant() : null;

    /// <summary>A transaction's explorer page, when the chain is known and the reference has the shape of its transaction ids.</summary>
    public static string? TxUrl(string chainId, string reference) =>
        Chains.TryGetValue(chainId, out var chain) && TxIds.TryGetValue(chainId.Split(':')[0], out var shape) && shape.IsMatch(reference)
            ? chain.Tx.Replace("{0}", reference)
            : null;
}
