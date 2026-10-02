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

    [Fact]
    public void A_reference_that_is_not_a_transaction_id_gets_no_explorer_link()
    {
        Assert.Null(ChainDirectory.TxUrl("eip155:999999", "0x" + new string('a', 64)));
        Assert.Null(ChainDirectory.TxUrl("eip155:42161", "not-a-hash"));
        Assert.Null(ChainDirectory.TxUrl("eip155:42161", new string('a', 64)));
        Assert.Null(ChainDirectory.TxUrl("tron:0xcd8690dc", "0x" + new string('b', 64)));
    }
}
