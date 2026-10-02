#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;
using NBitcoin;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <param name="UriParam">The BIP321 key carrying this rail's destination; null when it is the URI path (on-chain).</param>
public sealed record LnurlRail(string OptionType, PaymentMethodId PaymentMethodId, string Label, string PrettyName, string? UriParam)
{
    private const string Bech32 = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public bool OnChain => UriParam is null;

    public bool IsValidDestination(string destination, Network network)
    {
        switch (UriParam)
        {
            case null:
                try { BitcoinAddress.Create(destination, network); return true; }
                catch (FormatException) { return false; }
            case "ark":
            {
                var prefix = network.ChainName == ChainName.Mainnet ? "ark1" : "tark1";
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                // Core renders a destination into an unquoted title=, so the data part may hold nothing but bech32.
                var data = destination.Substring(prefix.Length);
                return data.Length > 0 && data.All(c => Bech32.Contains(char.ToLowerInvariant(c)));
            }
            default:
                return true;
        }
    }
}

/// <summary>The LNURL paymentOptions types one BIP321 URI can carry. Adding a rail is adding a row.</summary>
public static class LnurlRails
{
    public static readonly LnurlRail Arkade = new("arkade", new PaymentMethodId("LNURL-ARKADE"), "Arkade", "Arkade (LNURL)", "ark");
    public static readonly LnurlRail OnChain = new("onchain", new PaymentMethodId("LNURL-ONCHAIN"), "On-chain", "On-chain (LNURL)", null);
    public static readonly IReadOnlyList<LnurlRail> All = new[] { Arkade, OnChain };

    public static LnurlRail? For(PaymentMethodId pmi) => All.FirstOrDefault(r => r.PaymentMethodId == pmi);

    public static bool IsRail(PaymentMethodId pmi) => For(pmi) is not null;
}
