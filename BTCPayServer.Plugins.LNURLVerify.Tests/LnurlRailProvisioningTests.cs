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
    public void The_lnurl_is_read_from_the_enabled_lightning_connection_string()
    {
        Assert.Equal("alice@lnurl.example", LnurlRailProvisioning.LnurlValue(Store()));
        Assert.Null(LnurlRailProvisioning.LnurlValue(Store(excluded: "BTC-LN")));
        Assert.Null(LnurlRailProvisioning.LnurlValue(Store(ln: LndRest)));
        Assert.Null(LnurlRailProvisioning.LnurlValue(Store(ln: null)));
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
}
