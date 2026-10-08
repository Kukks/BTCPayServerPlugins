using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class ChainDirectoryTests
{
    [Theory]
    [InlineData("eip155:42161", "Arbitrum One")]
    [InlineData("solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1", "Solana Devnet")]
    [InlineData("tron:0xcd8690dc", "Tron Nile")]
    [InlineData("eip155:999999", "eip155:999999")]
    public void Networks_are_named_locally_with_the_raw_id_as_fallback(string chain, string label) =>
        Assert.Equal(label, ChainDirectory.Label(chain));

    [Fact]
    public void A_known_chain_links_its_transactions()
    {
        var evm = "0x" + new string('a', 64);
        var tron = new string('b', 64);
        var solana = new string('5', 88);
        Assert.Equal("https://sepolia.arbiscan.io/tx/" + evm, ChainDirectory.TxUrl("eip155:421614", evm));
        Assert.Equal("https://nile.tronscan.org/#/transaction/" + tron, ChainDirectory.TxUrl("tron:0xcd8690dc", tron));
        Assert.Equal($"https://solscan.io/tx/{solana}?cluster=devnet", ChainDirectory.TxUrl("solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1", solana));
    }

    // lnurl-server's FF_ASSETS chains: one it adds first must fail here rather than reach a payer as a raw CAIP-2 id.
    [Theory]
    [InlineData("eip155:1")]
    [InlineData("eip155:42161")]
    [InlineData("eip155:8453")]
    [InlineData("eip155:10")]
    [InlineData("eip155:137")]
    [InlineData("eip155:43114")]
    [InlineData("solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp")]
    [InlineData("tron:0x2b6653dc")]
    public void Every_fixedfloat_chain_is_named_and_links_its_deposit_transactions(string chain)
    {
        var depositTx = chain.Split(':')[0] switch { "eip155" => "0x" + new string('a', 64), "solana" => new string('5', 88), _ => new string('b', 64) };
        Assert.NotEqual(chain, ChainDirectory.Label(chain));
        Assert.NotNull(ChainDirectory.TxUrl(chain, depositTx));
    }

    [Fact]
    public void A_reference_that_is_not_a_transaction_id_gets_no_explorer_link()
    {
        Assert.Null(ChainDirectory.TxUrl("eip155:999999", "0x" + new string('a', 64)));
        Assert.Null(ChainDirectory.TxUrl("eip155:42161", "not-a-hash"));
        Assert.Null(ChainDirectory.TxUrl("eip155:42161", new string('a', 64)));
        Assert.Null(ChainDirectory.TxUrl("tron:0xcd8690dc", "0x" + new string('b', 64)));
    }
}
