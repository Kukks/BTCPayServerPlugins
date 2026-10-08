using System.Net;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class CheckoutRailsTests
{
    static readonly PaymentMethodId Ln = PaymentMethodId.Parse("BTC-LN"), Chain = PaymentMethodId.Parse("BTC-CHAIN");
    static readonly PaymentMethodId Arkade = LnurlRails.Arkade.PaymentMethodId;

    static PaymentPrompt Prompt(PaymentMethodId pmi, bool active) => new() { PaymentMethodId = pmi, Inactive = !active, Currency = "BTC" };

    static InvoiceEntity Invoice(params (PaymentMethodId Pmi, bool Active)[] prompts)
    {
        var invoice = new InvoiceEntity { Id = "inv", Currency = "BTC" };
        foreach (var (pmi, active) in prompts) invoice.SetPaymentPrompt(pmi, Prompt(pmi, active));
        return invoice;
    }

    static IReadOnlyList<PaymentPrompt> Mixed() => new[] { Prompt(Ln, true), Prompt(Chain, false), Prompt(Arkade, false) };

    [Fact]
    public void Only_an_invoice_holding_an_lnurl_rail_gets_the_checkout()
    {
        Assert.False(CheckoutRails.Applies(Invoice((Ln, true), (Chain, true))));
        Assert.True(CheckoutRails.Applies(Invoice((Ln, true), (Arkade, false))));
    }

    [Fact]
    public void Rails_run_lightning_onchain_lnurl_and_the_stores_own_wallet_wins_onchain()
    {
        var invoice = Invoice((Arkade, false), (LnurlRails.OnChain.PaymentMethodId, false), (Chain, true), (Ln, true));
        Assert.Equal(new[] { Ln, Chain, Arkade }, CheckoutRails.Rails(invoice).Select(p => p.PaymentMethodId));
    }

    [Fact]
    public void An_unset_setting_activates_every_inactive_rail_on_open()
    {
        var (activate, skipped) = CheckoutRails.Plan(Mixed(), null, new LnurlRailSettings().ActivateAllRailsOnOpen, _ => false);
        Assert.Equal(new[] { Chain, Arkade }, activate);
        Assert.Empty(skipped);
    }

    [Fact]
    public void With_the_setting_off_opening_activates_nothing() =>
        Assert.Empty(CheckoutRails.Plan(Mixed(), null, false, _ => false).Activate);

    [Fact]
    public void A_rail_that_failed_recently_is_not_retried_on_open()
    {
        var (activate, skipped) = CheckoutRails.Plan(Mixed(), null, true, p => p == Arkade);
        Assert.Equal(new[] { Chain }, activate);
        Assert.Equal(new[] { Arkade }, skipped);
    }

    [Fact]
    public void A_tap_activates_that_rail_even_after_a_failure_but_never_an_active_one()
    {
        Assert.Equal(new[] { Arkade }, CheckoutRails.Plan(Mixed(), "LNURL-ARKADE", false, _ => true).Activate);
        Assert.Empty(CheckoutRails.Plan(Mixed(), "BTC-LN", true, _ => false).Activate);
    }

    [Fact]
    public void A_failure_is_remembered_for_an_hour_per_invoice()
    {
        var failures = new RailActivationFailures();
        var now = DateTimeOffset.UtcNow;
        failures.Record("inv", Arkade, now);
        Assert.True(failures.Recent("inv", Arkade, now.AddMinutes(59)));
        Assert.False(failures.Recent("inv", Arkade, now.AddMinutes(61)));
        Assert.False(failures.Recent("other", Arkade, now));
    }

    [Fact]
    public async Task Overlapping_activations_of_one_rail_run_once()
    {
        var gate = new RailActivationGate();
        var release = new TaskCompletionSource<bool>();
        var calls = 0;
        Task<bool> Activate() { calls++; return release.Task; }

        var first = gate.Run("inv", Arkade, Activate);
        var second = gate.Run("inv", Arkade, Activate);
        release.SetResult(true);

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_finished_activation_does_not_absorb_the_next()
    {
        var gate = new RailActivationGate();
        var release = new TaskCompletionSource<bool>();
        var calls = 0;
        Task<bool> Activate() { calls++; return calls == 1 ? release.Task : Task.FromResult(false); }

        var first = gate.Run("inv", Arkade, Activate);
        var second = gate.Run("inv", Arkade, Activate);
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.False(await gate.Run("inv", Arkade, Activate));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Different_rails_and_invoices_activate_independently()
    {
        var gate = new RailActivationGate();
        var release = new TaskCompletionSource<bool>();
        var calls = 0;
        Task<bool> Activate() { calls++; return release.Task; }

        var runs = new[] { gate.Run("inv", Arkade, Activate), gate.Run("inv", Chain, Activate), gate.Run("other", Arkade, Activate) };
        Assert.Equal(3, calls);
        release.SetResult(true);

        Assert.DoesNotContain(false, await Task.WhenAll(runs));
    }

    // BTCPay's own forwarded-headers setup (Startup.cs): every sender trusted, the last hop applied.
    static IPAddress? PayerOf(string peer, string? forwardedFor, string? originalFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Connection.RemotePort = 40000;
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (originalFor is not null) context.Request.Headers["X-Original-For"] = originalFor;
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.All };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options)).ApplyForwarders(context);
        return PayerIp.Resolve(context);
    }

    [Theory]
    [InlineData("203.0.113.9", null, null, "203.0.113.9")]
    [InlineData("203.0.113.9", null, "10.0.0.1:5", "203.0.113.9")]
    [InlineData("127.0.0.1", "203.0.113.7", null, "203.0.113.7")]
    [InlineData("172.18.0.5", "198.51.100.1, 203.0.113.7", null, "203.0.113.7")]
    [InlineData("fd00::5", "203.0.113.7", null, "203.0.113.7")]
    [InlineData("fe80::5", "203.0.113.7", null, "203.0.113.7")]
    [InlineData("::ffff:192.168.1.2", "203.0.113.7", null, "203.0.113.7")]
    public void A_direct_connection_or_a_local_proxy_names_the_payer(string peer, string? forwardedFor, string? originalFor, string payer) =>
        Assert.Equal(IPAddress.Parse(payer), PayerOf(peer, forwardedFor, originalFor));

    [Theory]
    [InlineData("203.0.113.9", "198.51.100.1", null)]
    [InlineData("203.0.113.9", "198.51.100.1", "10.0.0.1:5")]
    [InlineData("203.0.113.9", null, "198.51.100.1:5")]
    [InlineData("2001:db8::5", "203.0.113.7", null)]
    [InlineData("203.0.113.9", null, "not-an-endpoint")]
    public void A_public_peer_or_an_unreadable_original_names_no_payer(string peer, string? forwardedFor, string? originalFor) =>
        Assert.Null(PayerOf(peer, forwardedFor, originalFor));
}
