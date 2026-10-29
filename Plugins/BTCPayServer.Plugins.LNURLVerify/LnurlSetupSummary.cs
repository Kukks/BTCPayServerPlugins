#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="Id">Null on the row for plain LUD-06 Lightning, which an LNURL listing no lightning option still takes.</param>
/// <param name="Toggle">The payment method the store's switch for this option turns on or off; null for Lightning itself.</param>
/// <param name="Locked">Why checkout cannot offer this option whatever the switch says.</param>
/// <param name="Transient">Why checkout would skip it right now, though the switch still decides.</param>
public sealed record LnurlSetupOption(string? Id, string Type, string Label, long? MinSendable, long? MaxSendable, bool Available,
    bool? Verifiable, string? Asset, string? Unit, string? Icon, string? UnitIcon, string? Toggle, bool Excluded, string? Locked,
    string? Transient)
{
    /// <summary>Why checkout would not offer this option; null when it would.</summary>
    public string? Reason => Locked ?? (Excluded ? LnurlSetupSummary.SwitchedOff : Transient);
    public bool Offered => Reason is null;
}

/// <summary>An LNURL as the Lightning setup page shows it: what it is, and what checkout would do with each of its options.</summary>
public sealed record LnurlSetupSummary(string Target, string Domain, bool Sends, long? MinSendable, long? MaxSendable,
    IReadOnlyList<LnurlSetupOption> Options)
{
    private const string Unavailable = "the LNURL reports it unavailable right now";
    private const string Unverifiable = "the LNURL marks it not verifiable, so its settlement could not be detected";
    public const string SwitchedOff = "switched off for this store";

    /// <summary>Reads the payRequest alone: a callback costs the LNURL an invoice (on lnurl-server, a swap), so verify and the network are left to Save's probe.</summary>
    public static async Task<LnurlSetupSummary> Read(string lnurl, StoreData store, TokenAssets assets, Network network, HttpClient http,
        CancellationToken ct)
    {
        var resolved = await LNURLVerifyConnectionStringHandler.ResolveCached(lnurl, network, http, ct);
        return Build(lnurl, resolved, await LNURLResolver.GetJson(http, resolved.PayEndpoint, ct), store, assets);
    }

    public static LnurlSetupSummary Build(string lnurl, ResolvedLnurl resolved, JObject pay, StoreData store, TokenAssets assets)
    {
        // The store as Save leaves it: its own configs and exclusions, with this LNURL as its Lightning.
        var saved = new StoreData { Id = store.Id, DerivationStrategies = store.DerivationStrategies, StoreBlob = store.StoreBlob };
        saved.SetPaymentMethodConfig(LnurlRailProvisioning.Lightning, new JObject { ["connectionString"] = "type=lnurl;value=" + lnurl });
        var blocked = LnurlRailProvisioning.LnurlValue(saved) is null ? "Lightning is disabled for this store" : null;
        var blob = saved.GetStoreBlob();
        var tokens = TokenOption.Parse(pay).ToDictionary(t => t.Id);
        var desiredTokens = LnurlRailProvisioning.DesiredTokens(saved, pay, assets);
        string? lightningId = null, lightningRefusal = null;
        try { lightningId = PaymentOption.PlanLightning(pay, 1, long.MaxValue).OptionId; }
        catch (NotSupportedException e) { lightningRefusal = e.Message; }

        (string Label, string? Locked, string? Transient, PaymentMethodId? Toggle, CaipAsset? Asset) Describe(PaymentOption? o)
        {
            if (o is null || o.IsLightning)
                return ("Lightning", lightningRefusal, o is null || o.Id == lightningId ? null
                    : o.Available ? $"Lightning uses the '{lightningId}' option" : Unavailable, null, null);
            if (LnurlRails.All.FirstOrDefault(r => r.OptionType.Equals(o.Type, StringComparison.OrdinalIgnoreCase)) is { } rail)
                return (rail.Label, o.Verifiable == false ? Unverifiable : LnurlRailProvisioning.Refusal(saved, rail),
                    o.Available ? null : Unavailable, rail.PaymentMethodId, null);
            // Unlike a rail, an unavailable token network stays on offer at checkout: LnurlTokenRequester treats it as a transient outage.
            if (tokens.TryGetValue(o.Id, out var token))
            {
                var pmi = TokenAssets.PaymentMethodIdOf(token.Unit.Code);
                return ($"{token.Unit.Code} on {ChainDirectory.Label(token.Asset.ChainId)}", o.Verifiable == false ? Unverifiable
                    : !desiredTokens.Contains(pmi) ? $"this server does not show {token.Unit.Code}: a server admin can add it to {TokenAssets.ConfigKey}"
                    : null, null, pmi, token.Asset);
            }
            return (o.Id, TokenNamespaces.All.ContainsKey(o.Type)
                ? "its asset or unit is not one this plugin can read" : $"this plugin does not support '{o.Type}'", null, null, null);
        }

        LnurlSetupOption Row(PaymentOption? o, long? min, long? max)
        {
            var (label, locked, transient, toggle, asset) = Describe(o);
            return new LnurlSetupOption(o?.Id, o?.Type ?? "lightning", label, min, max, o?.Available ?? true, o?.Verifiable, o?.Asset,
                o?.Unit, asset is null ? null : ChainDirectory.Icon(asset.ChainId), o?.Unit is { } unit ? ChainDirectory.TokenIcon(unit) : null,
                toggle?.ToString(), toggle is not null && blob.IsExcluded(toggle), blocked ?? locked, transient);
        }

        var (topMin, topMax) = (PaymentOption.Msat(pay["minSendable"]), PaymentOption.Msat(pay["maxSendable"]));
        var options = PaymentOption.Parse(pay);
        var rows = options.Select(o => Row(o, o.MinSendable ?? topMin, o.MaxSendable ?? topMax)).ToList();
        if (!options.Any(o => o.IsLightning)) rows.Insert(0, Row(null, topMin, topMax));
        return new LnurlSetupSummary(resolved.PayEndpoint.AbsoluteUri, resolved.DisplayHost,
            resolved.Capability == LnurlCapability.SendAndReceive, topMin, topMax, rows);
    }
}
