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
    public static string? LnurlValue(StoreData store) =>
        LnurlValue(store.GetPaymentMethodConfig(Lightning, onlyEnabled: true)?["connectionString"]?.Value<string>());

    public static string? LnurlValue(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return null;
        try
        {
            var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            return type == "lnurl" && kv.TryGetValue("value", out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The option types a payRequest offers to a rail: one it marks unverifiable can never be recorded.</summary>
    public static string[] OfferedTypes(JObject payRequest) =>
        PaymentOption.Parse(payRequest).Where(o => o.Verifiable != false).Select(o => o.Type).ToArray();

    public static IReadOnlyCollection<LnurlRail> Desired(StoreData store, params string[] offeredTypes)
    {
        var offered = new HashSet<string>(offeredTypes, StringComparer.OrdinalIgnoreCase);
        return LnurlRails.All.Where(r => offered.Contains(r.OptionType) && Refusal(store, r) is null).ToArray();
    }

    /// <summary>Why the store gets no rail even when its LNURL offers it; null when it does.</summary>
    public static string? Refusal(StoreData store, LnurlRail rail) =>
        LnurlValue(store) is null ? "the store's Lightning is not an enabled LNURL"
        : store.GetPaymentMethodConfig(NativeArkade) is not null ? "the store takes Arkade through the Arkade plugin, which replaces every LNURL rail"
        : rail.OnChain && store.GetPaymentMethodConfig(OnChain, onlyEnabled: true) is not null ? "the store's own on-chain wallet takes on-chain payments"
        : null;

    /// <summary>The configured assets the payRequest offers on a supported network not marked unverifiable.</summary>
    public static IReadOnlyCollection<PaymentMethodId> DesiredTokens(StoreData store, JObject payRequest, TokenAssets assets)
    {
        if (LnurlValue(store) is null) return Array.Empty<PaymentMethodId>();
        var offered = TokenOption.Parse(payRequest).Where(o => o.Verifiable != false).Select(o => o.Unit.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return assets.Codes.Where(offered.Contains).Select(TokenAssets.PaymentMethodIdOf).ToArray();
    }

    public static bool Reconcile(StoreData store, IReadOnlyCollection<LnurlRail> desired) =>
        Reconcile(store, LnurlRails.All.Select(r => r.PaymentMethodId), desired.Select(r => r.PaymentMethodId).ToArray());

    public static bool Reconcile(StoreData store, IEnumerable<PaymentMethodId> managed, IReadOnlyCollection<PaymentMethodId> desired)
    {
        var changed = false;
        foreach (var pmi in managed)
        {
            var want = desired.Contains(pmi);
            if ((store.GetPaymentMethodConfig(pmi) is not null) == want) continue;
            store.SetPaymentMethodConfig(pmi, want ? new JObject() : null);
            changed = true;
        }
        return changed;
    }
}
