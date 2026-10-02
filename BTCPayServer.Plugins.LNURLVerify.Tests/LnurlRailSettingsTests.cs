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

        Assert.True(LnurlRailSettings.ApplyEnabledRails(store, new[] { "LNURL-ONCHAIN" }));

        var blob = store.GetStoreBlob();
        Assert.True(blob.IsExcluded(ArkadeId));
        Assert.False(blob.IsExcluded(OnChainId));
        Assert.False(LnurlRailSettings.ApplyEnabledRails(store, new[] { "LNURL-ONCHAIN" }));
    }

    [Fact]
    public void Only_configured_rails_are_ever_touched()
    {
        var store = Store(ArkadeId);
        var seeded = store.GetStoreBlob();
        seeded.SetExcluded(CoreChain, true);
        store.SetStoreBlob(seeded);

        LnurlRailSettings.ApplyEnabledRails(store, new[] { "LNURL-ARKADE", "BTC-CHAIN" });

        var blob = store.GetStoreBlob();
        Assert.True(blob.IsExcluded(CoreChain));
        Assert.False(blob.IsExcluded(ArkadeId));
        Assert.False(blob.IsExcluded(OnChainId));
    }
}
