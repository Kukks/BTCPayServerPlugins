using System.Net.Http;
using BTCPayServer.Plugins.Electrum.Services;
using Xunit;

public class NbxRequestClassifierTests
{
    [Theory]
    [InlineData("GET",  "/v1/cryptos/BTC/derivations/SCHEME/balance", "",            NbxCallKind.WalletRead,       "SCHEME")]
    [InlineData("GET",  "/v1/cryptos/BTC/derivations/SCHEME/utxos",   "",            NbxCallKind.WalletRead,       "SCHEME")]
    [InlineData("GET",  "/v1/cryptos/BTC/derivations/SCHEME/addresses/unused", "reserve=true", NbxCallKind.WalletMutate, "SCHEME")]
    [InlineData("POST", "/v1/cryptos/BTC/derivations/SCHEME",         "",            NbxCallKind.TrackOrGenerate,  "SCHEME")]
    [InlineData("POST", "/v1/cryptos/BTC/transactions",               "",            NbxCallKind.Broadcast,        null)]
    [InlineData("GET",  "/v1/cryptos/BTC/status",                     "",            NbxCallKind.GlobalRead,       null)]
    public void Classifies(string method, string path, string query, NbxCallKind kind, string? wallet)
    {
        var call = NbxRequestClassifier.Classify(new HttpMethod(method), path, query);
        Assert.Equal(kind, call.Kind);
        Assert.Equal(wallet, call.WalletId);
    }
}
