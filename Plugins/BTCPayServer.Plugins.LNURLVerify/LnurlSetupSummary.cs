#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Newtonsoft.Json.Linq;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="Id">Null on the row for plain LUD-06 Lightning, which an LNURL listing no lightning option still takes.</param>
/// <param name="Reason">Why checkout would not offer this option; null when it would.</param>
public sealed record LnurlSetupOption(string? Id, string Type, string Label, long? MinSendable, long? MaxSendable, bool Available,
    bool? Verifiable, string? Asset, string? Unit, string? Reason)
{
    public bool Offered => Reason is null;
}

/// <summary>An LNURL as the Lightning setup page shows it: what it is, and what checkout would do with each of its options.</summary>
/// <param name="SaveError">Why BTCPay's own validation would refuse to save this LNURL; null when it would save it.</param>
public sealed record LnurlSetupSummary(string Target, string Domain, bool Sends, long? MinSendable, long? MaxSendable,
    string? SaveError, bool Verify, bool VerifyBatch, IReadOnlyList<LnurlSetupOption> Options)
{
    private const string Unavailable = "the LNURL reports it unavailable right now";
    private const string Unverifiable = "the LNURL marks it not verifiable, so its settlement could not be detected";

    /// <summary>Costs the LNURL one probe invoice, as saving does: verify is only learnt from a callback answer.</summary>
    public static async Task<LnurlSetupSummary> Read(string lnurl, StoreData store, TokenAssets assets, Network network, HttpClient http,
        CancellationToken ct)
    {
        var resolved = await LNURLVerifyConnectionStringHandler.ResolveCached(lnurl, network, http, ct);
        var pay = await LNURLResolver.GetJson(http, resolved.PayEndpoint, ct);
        return Build(lnurl, resolved, pay, await LNURLReceiver.Probe(http, pay, network, ct), store, assets);
    }

    public static LnurlSetupSummary Build(string lnurl, ResolvedLnurl resolved, JObject pay,
        (string? Error, bool Verify, string? VerifyBatch) probe, StoreData store, TokenAssets assets)
    {
        // The store as Save leaves it: its own configs and exclusions, with this LNURL as its Lightning.
        var saved = new StoreData { Id = store.Id, DerivationStrategies = store.DerivationStrategies, StoreBlob = store.StoreBlob };
        saved.SetPaymentMethodConfig(LnurlRailProvisioning.Lightning, new JObject { ["connectionString"] = "type=lnurl;value=" + lnurl });
        var blocked = probe.Error is not null ? "BTCPay will not save this LNURL"
            : LnurlRailProvisioning.LnurlValue(saved) is null ? "Lightning is disabled for this store" : null;
        var tokens = TokenOption.Parse(pay).ToDictionary(t => t.Id);
        var desiredTokens = LnurlRailProvisioning.DesiredTokens(saved, pay, assets);
        string? lightningId = null, lightningRefusal = null;
        try { lightningId = PaymentOption.PlanLightning(pay, 1, long.MaxValue).OptionId; }
        catch (NotSupportedException e) { lightningRefusal = e.Message; }

        (string Label, string? Reason) Describe(PaymentOption? o)
        {
            if (o is null || o.IsLightning)
                return ("Lightning", lightningRefusal ??
                    (o is null || o.Id == lightningId ? null : o.Available ? $"Lightning uses the '{lightningId}' option" : Unavailable));
            if (LnurlRails.All.FirstOrDefault(r => r.OptionType.Equals(o.Type, StringComparison.OrdinalIgnoreCase)) is { } rail)
                return (rail.Label, o.Verifiable == false ? Unverifiable
                    : LnurlRailProvisioning.Refusal(saved, rail) ?? (o.Available ? null : Unavailable));
            if (tokens.TryGetValue(o.Id, out var token))
                return ($"{token.Unit.Code} on {ChainDirectory.Label(token.Asset.ChainId)}", o.Verifiable == false ? Unverifiable
                    : !desiredTokens.Contains(TokenAssets.PaymentMethodIdOf(token.Unit.Code))
                        ? $"this server does not show {token.Unit.Code}: a server admin can add it to {TokenAssets.ConfigKey}"
                    : o.Available ? null : Unavailable);
            return (o.Id, TokenNamespaces.All.ContainsKey(o.Type)
                ? "its asset or unit is not one this plugin can read" : $"this plugin does not support '{o.Type}'");
        }

        LnurlSetupOption Row(PaymentOption? o, long? min, long? max)
        {
            var (label, reason) = Describe(o);
            return new LnurlSetupOption(o?.Id, o?.Type ?? "lightning", label, min, max, o?.Available ?? true, o?.Verifiable, o?.Asset,
                o?.Unit, blocked ?? reason);
        }

        var (topMin, topMax) = (PaymentOption.Msat(pay["minSendable"]), PaymentOption.Msat(pay["maxSendable"]));
        var options = PaymentOption.Parse(pay);
        var rows = options.Select(o => Row(o, o.MinSendable ?? topMin, o.MaxSendable ?? topMax)).ToList();
        if (!options.Any(o => o.IsLightning)) rows.Insert(0, Row(null, topMin, topMax));
        return new LnurlSetupSummary(resolved.PayEndpoint.AbsoluteUri, resolved.DisplayHost,
            resolved.Capability == LnurlCapability.SendAndReceive, topMin, topMax, probe.Error, probe.Verify, probe.VerifyBatch is not null, rows);
    }
}
