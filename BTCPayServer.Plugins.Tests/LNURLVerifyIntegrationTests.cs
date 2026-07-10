using System;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Tests.LNURLVerify;

// Integration tests for the LNURL Verify plugin.
//
// !!! NOT YET RUN / UNVERIFIED BEHAVIOUR !!!
// These require the ServerTester regtest Lightning stack (bitcoind + LN nodes + channels), the same
// harness the other ServerTester tests use. They were authored against the confirmed harness APIs
// (so they should compile — that checks the wiring), but their runtime behaviour has NOT been observed.
// Run them on a stack-equipped machine and fix from real failures before trusting them.
//
// Receive-leg strategy (no LNbits needed): BTCPay core already serves LNURL-pay with LUD-21 verify
// (LUD21Enabled defaults true), so a ServerTester store's own LN address IS a verify-capable endpoint.
// The plugin's client is driven DIRECTLY (the plugin assembly is ProjectReferenced) — it is NOT loaded
// into the host, so there is no DEBUG_PLUGINS dance. Because the shared poller (an IHostedService) only
// runs when the plugin is loaded into a host, settlement here is observed by polling GetInvoice (which
// polls the verify URL on demand) instead of via Listen().
[Trait("Integration", "Integration")]
public class LNURLVerifyIntegrationTests : UnitTestBase
{
    public LNURLVerifyIntegrationTests(ITestOutputHelper helper) : base(helper) { }

    [Fact]
    public async Task Receives_via_lnurl_pay_and_lud21_verify()
    {
        using var tester = CreateServerTester();
        await tester.StartAsync();
        await tester.EnsureChannelsSetup();

        // A store with the internal (merchant) LN node + an LN address => a verify-capable LNURL-pay endpoint.
        var user = tester.NewAccount();
        await user.GrantAccessAsync();
        user.RegisterLightningNode("BTC");
        await user.CreateLNAddress();

        // PayTester serves HTTP on localhost; pass the raw well-known LNURL-pay URL (the plugin accepts
        // http(s) endpoints, sidestepping the https-for-lightning-address assumption).
        var payUrl = new Uri(tester.PayTester.ServerUri, $".well-known/lnurlp/{user.LNAddress}").AbsoluteUri;

        var handler = new LNURLVerifyConnectionStringHandler(
            tester.PayTester.GetService<IHttpClientFactory>(),
            tester.PayTester.GetService<ILoggerFactory>());
        var client = handler.Create($"type=lnurl;value={payUrl}", Network.RegTest, out var error);
        Assert.Null(error);
        Assert.NotNull(client);

        var invoice = await client!.CreateInvoice(
            new LightMoney(1000, LightMoneyUnit.Satoshi), "lnurl-verify integration", TimeSpan.FromMinutes(10));
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.False(string.IsNullOrEmpty(invoice.BOLT11));

        // Pay the returned bolt11 from the customer node.
        await tester.CustomerLightningD.Pay(invoice.BOLT11);

        // The plugin should observe settlement via the LUD-21 verify endpoint, with a validated preimage.
        await TestUtils.EventuallyAsync(async () =>
        {
            var updated = await client.GetInvoice(invoice.PaymentHash);
            Assert.NotNull(updated);
            Assert.Equal(LightningInvoiceStatus.Paid, updated!.Status);
            Assert.False(string.IsNullOrEmpty(updated.Preimage));
        });
    }

    // TODO (send leg — not written): exercising Pay(bolt11) needs an LNURL-WITHDRAW endpoint that carries
    // a payLink. BTCPay core does not serve LNURL-withdraw-with-payLink, so this leg needs an LNbits (or
    // equivalent) regtest service added to the compose stack. Flow to implement:
    //   1. Stand up LNbits on regtest wired to the tester bitcoind/LN; create a REUSABLE withdraw link
    //      whose response includes a payLink.
    //   2. handler.Create("type=lnurl;value=<lnurlw>") => client with Capability == SendAndReceive.
    //   3. Create a bolt11 on the merchant node; client.Pay(bolt11) twice (verifies the k1 refresh).
    //   4. Assert PayResult.Ok for both and that the merchant node received the payments / balance dropped.
}
