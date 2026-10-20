using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class CaipAssetTests
{
    [Fact]
    public void A_caip19_id_splits_into_its_network_and_token()
    {
        var asset = CaipAsset.Parse("eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9")!;
        Assert.Equal(("eip155", "42161", "erc20", "0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9"),
            (asset.Namespace, asset.ChainReference, asset.AssetNamespace, asset.AssetReference));
        Assert.Equal("eip155:42161", asset.ChainId);
        Assert.Equal("eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9", asset.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eip155:42161")]
    [InlineData("EIP155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9")]
    [InlineData("eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\n")]
    [InlineData("eip155:42161/erc20:<script>")]
    [InlineData("eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9/1")]
    public void Malformed_ids_are_refused(string? id) => Assert.Null(CaipAsset.Parse(id));
}
