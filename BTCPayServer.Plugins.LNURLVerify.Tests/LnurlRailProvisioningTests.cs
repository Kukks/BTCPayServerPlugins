using BTCPayServer.Data;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailProvisioningTests
{
    static readonly string[] Everything = { "lightning", "arkade", "onchain" };
    const string LndRest = "type=lnd-rest;server=https://lnd.example/";

    static StoreData Store(string? ln = "type=lnurl;value=alice@lnurl.example", bool chain = false, bool nativeArkade = false,
        params string[] excluded)
    {
        var strategies = new JObject();
        if (ln is not null) strategies["BTC-LN"] = new JObject { ["connectionString"] = ln };
        if (chain) strategies["BTC-CHAIN"] = new JObject();
        if (nativeArkade) strategies["ARKADE"] = new JObject();
        var store = new StoreData { Id = "store", DerivationStrategies = strategies.ToString() };
        var blob = store.GetStoreBlob();
        foreach (var e in excluded) blob.SetExcluded(PaymentMethodId.Parse(e), true);
        store.SetStoreBlob(blob);
        return store;
    }

    static string[] Desired(StoreData store, params string[] offered) =>
        LnurlRailProvisioning.Desired(store, offered).Select(r => r.PaymentMethodId.ToString()).ToArray();

    [Fact]
    public void An_option_marked_unverifiable_is_never_provisioned()
    {
        var pay = JObject.Parse("{\"paymentOptions\":[{\"id\":\"lightning\",\"type\":\"lightning\",\"verifiable\":true}," +
                                "{\"id\":\"arkade\",\"type\":\"arkade\",\"verifiable\":true},{\"id\":\"onchain\",\"type\":\"onchain\",\"verifiable\":false}]}");
        Assert.Equal(new[] { "LNURL-ARKADE" }, Desired(Store(), LnurlRailProvisioning.OfferedTypes(pay)));
    }

    [Fact]
    public void An_option_that_states_nothing_about_verify_is_still_provisioned()
    {
        var pay = JObject.Parse("{\"paymentOptions\":[{\"id\":\"arkade\",\"type\":\"arkade\"},{\"id\":\"onchain\",\"type\":\"onchain\"}]}");
        Assert.Equal(new[] { "LNURL-ARKADE", "LNURL-ONCHAIN" }, Desired(Store(), LnurlRailProvisioning.OfferedTypes(pay)));
    }

    [Fact]
    public void The_lnurl_is_read_from_the_enabled_lightning_connection_string()
    {
        Assert.Equal("alice@lnurl.example", LnurlRailProvisioning.LnurlValue(Store()));
        Assert.Null(LnurlRailProvisioning.LnurlValue(Store(excluded: "BTC-LN")));
        Assert.Null(LnurlRailProvisioning.LnurlValue(Store(ln: LndRest)));
        Assert.Null(LnurlRailProvisioning.LnurlValue(Store(ln: null)));
    }

    [Fact]
    public void A_connection_string_names_an_lnurl_only_when_its_type_is_lnurl()
    {
        Assert.Equal("bob@lnurl.example", LnurlRailProvisioning.LnurlValue("type=lnurl;value=bob@lnurl.example"));
        Assert.Null(LnurlRailProvisioning.LnurlValue(LndRest));
        Assert.Null(LnurlRailProvisioning.LnurlValue("type=lnurl;value="));
        Assert.Null(LnurlRailProvisioning.LnurlValue((string?)null));
    }

    [Fact]
    public void A_store_without_a_wallet_gets_every_advertised_rail() =>
        Assert.Equal(new[] { "LNURL-ARKADE", "LNURL-ONCHAIN" }, Desired(Store(), Everything));

    [Fact]
    public void A_store_with_an_onchain_wallet_never_gets_the_lnurl_onchain_rail() =>
        Assert.Equal(new[] { "LNURL-ARKADE" }, Desired(Store(chain: true), Everything));

    [Fact]
    public void An_excluded_wallet_brings_the_lnurl_onchain_rail_back() =>
        Assert.Equal(new[] { "LNURL-ARKADE", "LNURL-ONCHAIN" }, Desired(Store(chain: true, excluded: "BTC-CHAIN"), Everything));

    [Fact]
    public void Only_advertised_option_types_are_provisioned()
    {
        Assert.Equal(new[] { "LNURL-ARKADE" }, Desired(Store(), "lightning", "arkade"));
        Assert.Empty(Desired(Store(), "lightning"));
        Assert.Empty(Desired(Store()));
    }

    [Fact]
    public void Stores_without_an_lnurl_backend_or_with_native_arkade_get_nothing()
    {
        Assert.Empty(Desired(Store(nativeArkade: true), Everything));
        Assert.Empty(Desired(Store(ln: LndRest), Everything));
        Assert.Empty(Desired(Store(excluded: "BTC-LN"), Everything));
    }

    [Fact]
    public void Reconcile_writes_once_then_finds_nothing_to_do()
    {
        var store = Store();
        Assert.True(LnurlRailProvisioning.Reconcile(store, LnurlRailProvisioning.Desired(store, Everything)));
        Assert.NotNull(store.GetPaymentMethodConfig(LnurlRails.Arkade.PaymentMethodId));
        Assert.False(LnurlRailProvisioning.Reconcile(store, LnurlRailProvisioning.Desired(store, Everything)));
    }

    [Fact]
    public void Reconcile_removes_a_rail_the_lnurl_stopped_advertising()
    {
        var store = Store();
        LnurlRailProvisioning.Reconcile(store, LnurlRailProvisioning.Desired(store, Everything));
        Assert.True(LnurlRailProvisioning.Reconcile(store, LnurlRailProvisioning.Desired(store, "lightning", "arkade")));
        Assert.Null(store.GetPaymentMethodConfig(LnurlRails.OnChain.PaymentMethodId));
        Assert.NotNull(store.GetPaymentMethodConfig(LnurlRails.Arkade.PaymentMethodId));
    }

    static readonly TokenAssets Assets = TokenAssets.Parse("USDT,USDC").Assets;
    const string ArbitrumUsdt = "{\"id\":\"usdt-arbitrum\",\"type\":\"eip155\",\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\"}";
    const string SolanaUsdc = "{\"id\":\"usdc-solana\",\"type\":\"solana\",\"asset\":\"solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp/token:EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v\",\"unit\":\"USDC\",\"verifiable\":false}";
    const string BaseEurc = "{\"id\":\"eurc-base\",\"type\":\"eip155\",\"asset\":\"eip155:8453/erc20:0x60a3E35Cc302bFA44Cb288Bc5a4F316Fdb1adb42\",\"unit\":\"EURC\"}";

    static JObject TokenPay(params string[] options) => JObject.Parse(
        "{\"units\":[{\"code\":\"USDT\",\"decimals\":6},{\"code\":\"USDC\",\"decimals\":6},{\"code\":\"EURC\",\"decimals\":6}],\"paymentOptions\":[" +
        string.Join(",", options) + "]}");

    [Fact]
    public void A_configured_asset_is_provisioned_while_one_of_its_networks_is_verifiable() =>
        Assert.Equal(new[] { "LNURL-USDT" },
            LnurlRailProvisioning.DesiredTokens(Store(), TokenPay(ArbitrumUsdt, SolanaUsdc, BaseEurc), Assets).Select(p => p.ToString()));

    // lnurl-server's FixedFloat rails (its spec §14): the whole case for their unit codes is that a stock install offers them.
    static readonly JObject FixedFloat = JObject.Parse(
        "{\"tag\":\"payRequest\",\"minSendable\":1000,\"maxSendable\":100000000,\"units\":[{\"code\":\"USDT\",\"decimals\":6,\"name\":\"Tether USD\"}," +
        "{\"code\":\"USDC\",\"decimals\":6,\"name\":\"USD Coin\"}],\"paymentOptions\":[{\"id\":\"ff-usdtarbitrum\",\"type\":\"eip155\"," +
        "\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\",\"provider\":\"FixedFloat\",\"verifiable\":true," +
        "\"minSendable\":2844000,\"maxSendable\":100000000},{\"id\":\"ff-usdcarbitrum\",\"type\":\"eip155\"," +
        "\"asset\":\"eip155:42161/erc20:0xaf88d065e77c8cC2239327C5EDb3A432268e5831\",\"unit\":\"USDC\",\"provider\":\"FixedFloat\",\"verifiable\":true," +
        "\"minSendable\":2863000,\"maxSendable\":100000000}]}");

    [Fact]
    public void A_stock_install_offers_fixedfloat_usdt_and_usdc_with_no_configuration()
    {
        Assert.Equal(new[] { "LNURL-USDT", "LNURL-USDC" },
            LnurlRailProvisioning.DesiredTokens(Store(), FixedFloat, TokenAssets.Parse(null).Assets).Select(p => p.ToString()));
        Assert.Equal(new[] { ("ff-usdtarbitrum", "USDT", 2_844_000L), ("ff-usdcarbitrum", "USDC", 2_863_000L) },
            TokenOption.Parse(FixedFloat).Select(o => (o.Id, o.Unit.Code, o.MinSendable!.Value)));
    }

    [Fact]
    public void Stores_without_an_lnurl_backend_get_no_tokens() =>
        Assert.Empty(LnurlRailProvisioning.DesiredTokens(Store(ln: LndRest), TokenPay(ArbitrumUsdt), Assets));

    [Fact]
    public void Token_reconcile_adds_and_removes_only_token_configs()
    {
        var store = Store();
        LnurlRailProvisioning.Reconcile(store, LnurlRailProvisioning.Desired(store, Everything));
        var usdt = TokenAssets.PaymentMethodIdOf("USDT");

        Assert.True(LnurlRailProvisioning.Reconcile(store, Assets.PaymentMethodIds, new[] { usdt }));
        Assert.NotNull(store.GetPaymentMethodConfig(usdt));
        Assert.Null(store.GetPaymentMethodConfig(TokenAssets.PaymentMethodIdOf("USDC")));

        Assert.True(LnurlRailProvisioning.Reconcile(store, Assets.PaymentMethodIds, Array.Empty<PaymentMethodId>()));
        Assert.Null(store.GetPaymentMethodConfig(usdt));
        Assert.NotNull(store.GetPaymentMethodConfig(LnurlRails.Arkade.PaymentMethodId));
    }
}
