using BTCPayServer.Payments;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class Bip321Tests
{
    static KeyValuePair<string, string> KV(string key, string value) => new(key, value);

    static RailState Ln(bool active = true) =>
        new(PaymentMethodId.Parse("BTC-LN"), "Lightning", active, "lnbcrt1ln", active ? "lightning:lnbcrt1ln" : null, "lightning");
    static RailState Chain() =>
        new(PaymentMethodId.Parse("BTC-CHAIN"), "On-chain", true, "bcrt1qaddr", "bitcoin:bcrt1qaddr?amount=0.0002&pj=https://x/pj", null);
    static RailState Ark(bool active = true) =>
        new(LnurlRails.Arkade.PaymentMethodId, "Arkade", active, "tark1qdest", active ? "bitcoin:?amount=0.0001&ark=tark1qdest" : null, "ark");

    [Fact]
    public void Build_puts_the_amount_first_and_the_address_in_the_path() =>
        Assert.Equal("bitcoin:bcrt1qaddr?amount=0.0001&ark=tark1qdest",
            Bip321.Build("bcrt1qaddr", 0.00010000m, new[] { KV("ark", "tark1qdest") }));

    [Fact]
    public void Build_without_an_address_starts_the_query_directly() =>
        Assert.Equal("bitcoin:?amount=1&lightning=lnbcrt1ln", Bip321.Build(null, 1m, new[] { KV("lightning", "lnbcrt1ln") }));

    [Fact]
    public void Amounts_are_invariant_btc_without_trailing_zeros()
    {
        Assert.Equal("0.00000001", Bip321.Amount(0.00000001m));
        Assert.Equal("21", Bip321.Amount(21.00000000m));
    }

    [Fact]
    public void Extend_keeps_existing_parameters_and_skips_a_key_already_present() =>
        Assert.Equal("bitcoin:bcrt1qaddr?amount=0.0001&lightning=lnbcrt1old&ark=tark1qdest",
            Bip321.Extend("bitcoin:bcrt1qaddr?amount=0.0001&lightning=lnbcrt1old",
                new[] { KV("lightning", "lnbcrt1new"), KV("ark", "tark1qdest") }));

    [Fact]
    public void Qr_uppercases_only_a_bech32_address()
    {
        Assert.Equal("bitcoin:BCRT1QADDR?amount=0.0001&ark=tark1qdest", Bip321.Qr("bitcoin:bcrt1qaddr?amount=0.0001&ark=tark1qdest"));
        Assert.Equal("bitcoin:2NBase58?amount=1", Bip321.Qr("bitcoin:2NBase58?amount=1"));
        Assert.Equal("bitcoin:?amount=1&ark=tark1qdest", Bip321.Qr("bitcoin:?amount=1&ark=tark1qdest"));
        Assert.Equal("lightning:LNBCRT1LN", Bip321.Qr("lightning:lnbcrt1ln"));
    }

    [Fact]
    public void A_lone_active_rail_keeps_its_own_uri() =>
        Assert.Equal("lightning:lnbcrt1ln", Bip321.Merge(new[] { Ln(), Ark(active: false) }, 0.0001m));

    [Fact]
    public void Merge_extends_the_onchain_link_with_lightning_then_ark() =>
        Assert.Equal("bitcoin:bcrt1qaddr?amount=0.0002&pj=https://x/pj&lightning=lnbcrt1ln&ark=tark1qdest",
            Bip321.Merge(new[] { Ln(), Chain(), Ark() }, 0.0001m));

    [Fact]
    public void Merge_without_onchain_carries_the_given_amount() =>
        Assert.Equal("bitcoin:?amount=0.0001&lightning=lnbcrt1ln&ark=tark1qdest", Bip321.Merge(new[] { Ln(), Ark() }, 0.0001m));

    [Fact]
    public void Merge_of_nothing_active_is_null() => Assert.Null(Bip321.Merge(new[] { Ln(false), Ark(false) }, 0.0001m));
}
