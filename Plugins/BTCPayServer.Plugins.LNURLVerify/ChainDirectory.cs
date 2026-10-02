#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Network names and explorers, resolved locally from CAIP-2 so the LNURL never has to send them.</summary>
public static class ChainDirectory
{
    private static readonly Dictionary<string, (string Label, string Tx)> Chains = new()
    {
        ["eip155:1"] = ("Ethereum", "https://etherscan.io/tx/{0}"),
        ["eip155:11155111"] = ("Sepolia", "https://sepolia.etherscan.io/tx/{0}"),
        ["eip155:42161"] = ("Arbitrum One", "https://arbiscan.io/tx/{0}"),
        ["eip155:421614"] = ("Arbitrum Sepolia", "https://sepolia.arbiscan.io/tx/{0}"),
        ["eip155:8453"] = ("Base", "https://basescan.org/tx/{0}"),
        ["eip155:84532"] = ("Base Sepolia", "https://sepolia.basescan.org/tx/{0}"),
        ["eip155:10"] = ("Optimism", "https://optimistic.etherscan.io/tx/{0}"),
        ["eip155:11155420"] = ("OP Sepolia", "https://sepolia-optimism.etherscan.io/tx/{0}"),
        ["eip155:137"] = ("Polygon", "https://polygonscan.com/tx/{0}"),
        ["eip155:80002"] = ("Polygon Amoy", "https://amoy.polygonscan.com/tx/{0}"),
        ["eip155:56"] = ("BNB Smart Chain", "https://bscscan.com/tx/{0}"),
        ["eip155:43114"] = ("Avalanche", "https://snowtrace.io/tx/{0}"),
        ["solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp"] = ("Solana", "https://solscan.io/tx/{0}"),
        ["solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1"] = ("Solana Devnet", "https://solscan.io/tx/{0}?cluster=devnet"),
        ["solana:4uhcVJyU9pJkvQyS88uRDiswHXSCkY3z"] = ("Solana Testnet", "https://solscan.io/tx/{0}?cluster=testnet"),
        ["tron:0x2b6653dc"] = ("Tron", "https://tronscan.org/#/transaction/{0}"),
        ["tron:0xcd8690dc"] = ("Tron Nile", "https://nile.tronscan.org/#/transaction/{0}"),
        ["tron:0x94a9059e"] = ("Tron Shasta", "https://shasta.tronscan.org/#/transaction/{0}")
    };

    private static readonly Dictionary<string, Regex> TxIds = new()
    {
        ["eip155"] = new(@"\A0x[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant),
        ["solana"] = new(@"\A[1-9A-HJ-NP-Za-km-z]{64,88}\z", RegexOptions.CultureInvariant),
        ["tron"] = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
    };

    public static string Label(string chainId) => Chains.TryGetValue(chainId, out var chain) ? chain.Label : chainId;

    /// <summary>A transaction's explorer page, when the chain is known and the reference has the shape of its transaction ids.</summary>
    public static string? TxUrl(string chainId, string reference) =>
        Chains.TryGetValue(chainId, out var chain) && TxIds.TryGetValue(chainId.Split(':')[0], out var shape) && shape.IsMatch(reference)
            ? chain.Tx.Replace("{0}", reference)
            : null;
}
