using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TokenNamespacesTests
{
    const string EvmTo = "0x1111111111111111111111111111111111111111";
    const string SolanaTo = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";
    const string TronTo = "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7";

    static string? Uri(string id, string to, string baseUnits, int decimals)
    {
        var asset = CaipAsset.Parse(id)!;
        return TokenNamespaces.For(asset)!.Uri(asset, to, baseUnits, decimals);
    }

    [Fact]
    public void An_evm_token_pays_through_an_eip681_transfer() =>
        Assert.Equal("ethereum:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9@42161/transfer?address=0x1111111111111111111111111111111111111111&uint256=63360000",
            Uri("eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9", EvmTo, "63360000", 6));

    [Fact]
    public void A_solana_token_pays_through_solana_pay_in_whole_units() =>
        Assert.Equal("solana:9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM?amount=63.36&spl-token=4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU",
            Uri("solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1/token:4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU", SolanaTo, "63360000", 6));

    [Fact]
    public void A_tron_token_has_no_uri_so_its_qr_is_the_recipient() =>
        Assert.Null(Uri("tron:0xcd8690dc/trc20:TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf", TronTo, "63360000", 6));

    [Fact]
    public void A_new_chain_in_a_supported_namespace_needs_no_code() =>
        Assert.Equal("ethereum:0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913@8453/transfer?address=0x1111111111111111111111111111111111111111&uint256=5",
            Uri("eip155:8453/erc20:0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", EvmTo, "5", 6));

    [Theory]
    [InlineData("eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb")]
    [InlineData("eip155:42161/slip44:60")]
    [InlineData("eip155:0x2a/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9")]
    [InlineData("solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1/token:0OIl")]
    [InlineData("tron:0x2b6653dc/trc20:TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6u")]
    [InlineData("cosmos:cosmoshub-4/slip44:118")]
    public void Unsupported_or_malformed_tokens_have_no_namespace(string id) =>
        Assert.Null(TokenNamespaces.For(CaipAsset.Parse(id)!));

    [Theory]
    [InlineData("eip155", EvmTo, true)]
    [InlineData("eip155", "0x111111111111111111111111111111111111111g", false)]
    [InlineData("eip155", "1111111111111111111111111111111111111111", false)]
    [InlineData("solana", SolanaTo, true)]
    [InlineData("solana", "<b>9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9z</b>", false)]
    [InlineData("solana", " TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", false)]
    [InlineData("solana", "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA\n", false)]
    [InlineData("solana", "ŔokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", false)]
    [InlineData("tron", TronTo, true)]
    [InlineData("tron", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6u", false)]
    [InlineData("tron", "0x74472e7d35395a6b5add427eecb7f4b62ad2b071", false)]
    [InlineData("tron", "TŌa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7", false)]
    public void Recipients_are_checked_per_namespace(string ns, string address, bool valid) =>
        Assert.Equal(valid, TokenNamespaces.All[ns].IsValidAddress(address));
}
