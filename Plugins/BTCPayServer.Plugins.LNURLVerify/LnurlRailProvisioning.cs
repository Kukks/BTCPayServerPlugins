#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

public static class LnurlRailProvisioning
{
    public static readonly PaymentMethodId Lightning = PaymentTypes.LN.GetPaymentMethodId("BTC");
    public static readonly PaymentMethodId OnChain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
    private static readonly PaymentMethodId NativeArkade = new("ARKADE");

    /// <summary>The LNURL behind the store's enabled BTC-LN; null when its backend is not this plugin's.</summary>
    public static string? LnurlValue(StoreData store)
    {
        var cs = store.GetPaymentMethodConfig(Lightning, onlyEnabled: true)?["connectionString"]?.Value<string>();
        if (string.IsNullOrEmpty(cs)) return null;
        try
        {
            var kv = LightningConnectionStringHelper.ExtractValues(cs, out var type);
            return type == "lnurl" && kv.TryGetValue("value", out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The option types a payRequest offers to a rail: one it marks unverifiable can never be recorded.</summary>
    public static string[] OfferedTypes(JObject payRequest) =>
        PaymentOption.Parse(payRequest).Where(o => o.Verifiable != false).Select(o => o.Type).ToArray();

    public static IReadOnlyCollection<LnurlRail> Desired(StoreData store, params string[] offeredTypes)
    {
        if (LnurlValue(store) is null || store.GetPaymentMethodConfig(NativeArkade) is not null)
            return Array.Empty<LnurlRail>();
        var chainUsable = store.GetPaymentMethodConfig(OnChain, onlyEnabled: true) is not null;
        var offered = new HashSet<string>(offeredTypes, StringComparer.OrdinalIgnoreCase);
        return LnurlRails.All.Where(r => offered.Contains(r.OptionType) && !(r.OnChain && chainUsable)).ToArray();
    }

    public static bool Reconcile(StoreData store, IReadOnlyCollection<LnurlRail> desired)
    {
        var changed = false;
        foreach (var rail in LnurlRails.All)
        {
            var want = desired.Contains(rail);
            if ((store.GetPaymentMethodConfig(rail.PaymentMethodId) is not null) == want) continue;
            store.SetPaymentMethodConfig(rail.PaymentMethodId, want ? new JObject() : null);
            changed = true;
        }
        return changed;
    }
}
