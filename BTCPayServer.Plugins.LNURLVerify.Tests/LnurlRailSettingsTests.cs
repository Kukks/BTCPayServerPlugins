using BTCPayServer.Data;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailSettingsTests
{
    static readonly PaymentMethodId ArkadeId = LnurlRails.Arkade.PaymentMethodId;
    static readonly PaymentMethodId OnChainId = LnurlRails.OnChain.PaymentMethodId;
    static readonly PaymentMethodId CoreChain = PaymentMethodId.Parse("BTC-CHAIN");
    static readonly PaymentMethodId[] Managed =
        LnurlRails.All.Select(r => r.PaymentMethodId).Append(TokenAssets.PaymentMethodIdOf("USDT")).ToArray();

    static StoreData Store(params PaymentMethodId[] configured)
    {
        var store = new StoreData { Id = "store" };
        foreach (var pmi in configured) store.SetPaymentMethodConfig(pmi, new JObject());
        return store;
    }

    [Fact]
    public void Switching_a_rail_off_excludes_only_that_rail()
    {
        var store = Store(ArkadeId, OnChainId);
        var configured = new[] { "LNURL-ARKADE", "LNURL-ONCHAIN" };

        Assert.True(LnurlRailSettings.ApplyEnabledRails(store, Managed, shown: configured, enabled: new[] { "LNURL-ONCHAIN" }));

        var blob = store.GetStoreBlob();
        Assert.True(blob.IsExcluded(ArkadeId));
        Assert.False(blob.IsExcluded(OnChainId));
        Assert.False(LnurlRailSettings.ApplyEnabledRails(store, Managed, shown: configured, enabled: new[] { "LNURL-ONCHAIN" }));
    }

    [Fact]
    public void Only_configured_rails_are_ever_touched()
    {
        var store = Store(ArkadeId);
        var seeded = store.GetStoreBlob();
        seeded.SetExcluded(CoreChain, true);
        store.SetStoreBlob(seeded);

        LnurlRailSettings.ApplyEnabledRails(store, Managed, shown: new[] { "LNURL-ARKADE" }, enabled: new[] { "LNURL-ARKADE", "BTC-CHAIN" });

        var blob = store.GetStoreBlob();
        Assert.True(blob.IsExcluded(CoreChain));
        Assert.False(blob.IsExcluded(ArkadeId));
        Assert.False(blob.IsExcluded(OnChainId));
    }

    [Fact]
    public void A_rail_the_page_never_showed_is_left_alone()
    {
        var store = Store(ArkadeId, OnChainId);

        LnurlRailSettings.ApplyEnabledRails(store, Managed, shown: new[] { "LNURL-ARKADE" }, enabled: Array.Empty<string>());

        var blob = store.GetStoreBlob();
        Assert.True(blob.IsExcluded(ArkadeId));
        Assert.False(blob.IsExcluded(OnChainId));
    }

    [Fact]
    public void A_token_asset_switches_off_like_a_rail()
    {
        var usdt = TokenAssets.PaymentMethodIdOf("USDT");
        var store = Store(ArkadeId, usdt);
        Assert.True(LnurlRailSettings.ApplyEnabledRails(store, Managed, shown: new[] { "LNURL-ARKADE", "LNURL-USDT" }, enabled: new[] { "LNURL-ARKADE" }));
        Assert.True(store.GetStoreBlob().IsExcluded(usdt));
        Assert.False(store.GetStoreBlob().IsExcluded(ArkadeId));
    }
}
