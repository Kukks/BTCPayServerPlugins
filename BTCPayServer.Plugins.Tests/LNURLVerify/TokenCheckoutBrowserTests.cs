using System.Runtime.CompilerServices;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.LNURLVerify;
using BTCPayServer.Services.Stores;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Tests.LNURLVerify;

// Chromium is launched directly because PlaywrightTester turns the CSP off, and the CSP is what these tests prove.
// Needs the BTCPay test stack; see VERIFICATION.md §6.
[Trait("Integration", "Integration")]
public class TokenCheckoutBrowserTests : UnitTestBase
{
    public TokenCheckoutBrowserTests(ITestOutputHelper helper) : base(helper) { }

    [Fact(Timeout = 300_000)]
    public async Task A_payer_sees_each_network_and_a_settled_quote_pays_the_invoice()
    {
        await using var stub = await StubLnurl.Start();
        using var tester = await Start();
        var (user, invoiceId) = await Invoice(tester, stub);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await OpenTokenTab(page, tester, invoiceId);
        await Expect(page.Locator("#LnurlTokenNetworks button")).ToHaveTextAsync(new[] { "Arbitrum Sepolia", "Solana Devnet", "Tron Nile" });

        var evm = $"ethereum:{StubLnurl.ArbitrumToken}@421614/transfer?address={StubLnurl.EvmRecipient}&uint256=63360000";
        await page.Locator("#LnurlTokenNetworks button[data-network='usdt-arbitrum']").ClickAsync();
        await Expect(page.Locator(".qr-container")).ToHaveAttributeAsync("data-qr-value", evm);
        await Expect(page.Locator("#LnurlTokenOpenWallet")).ToHaveAttributeAsync("href", evm);
        await Expect(page.Locator("#LnurlTokenAmount")).ToHaveTextAsync("63.36 USDT on Arbitrum Sepolia");

        var solana = $"solana:{StubLnurl.SolanaRecipient}?amount=63.36&spl-token={StubLnurl.SolanaMint}";
        await page.Locator("#LnurlTokenNetworks button[data-network='usdt-solana']").ClickAsync();
        await Expect(page.Locator(".qr-container")).ToHaveAttributeAsync("data-qr-value", solana);
        await Expect(page.Locator("#LnurlTokenOpenWallet")).ToHaveAttributeAsync("href", solana);

        await page.Locator("#LnurlTokenNetworks button[data-network='usdt-tron']").ClickAsync();
        await Expect(page.Locator(".qr-container")).ToHaveAttributeAsync("data-qr-value", StubLnurl.TronRecipient);
        await Expect(page.Locator("#LnurlTokenOpenWallet")).ToHaveCountAsync(0);

        var txHash = "0x" + new string('a', 64);
        stub.Settle("usdt-arbitrum", txHash);
        var client = await user.CreateClient();
        await TestUtils.EventuallyAsync(async () =>
            Assert.Equal(InvoiceStatus.Settled, (await client.GetInvoice(invoiceId)).Status), 60_000);

        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "login").AbsoluteUri);
        await page.FillAsync("#Email", user.RegisterDetails.Email);
        await page.FillAsync("#Password", user.RegisterDetails.Password);
        await page.ClickAsync("#LoginButton");
        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "invoices/" + invoiceId).AbsoluteUri);
        var row = page.Locator("section:has(> h5:text('LNURL rail payments')) tbody tr");
        await Expect(row).ToContainTextAsync("Arbitrum Sepolia");
        await Expect(row.Locator($"a[href='https://sepolia.arbiscan.io/tx/{txHash}']")).ToHaveCountAsync(1);
    }

    [Fact(Timeout = 300_000)]
    public async Task Connect_wallet_opens_a_walletconnect_pairing_under_the_checkout_csp()
    {
        var projectId = Environment.GetEnvironmentVariable("WALLETCONNECT_PROJECT_ID")
                        ?? throw new InvalidOperationException("Set WALLETCONNECT_PROJECT_ID to a Reown project ID (see VERIFICATION.md §6).");
        await using var stub = await StubLnurl.Start();
        using var tester = await Start();
        await tester.PayTester.GetService<ISettingsRepository>().UpdateSetting(new LnurlTokenServerSettings { WalletConnectProjectId = projectId });
        var (_, invoiceId) = await Invoice(tester, stub);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var refused = new List<string>();
        // Chromium 149 words a violation "Loading the script ... violates the following Content Security Policy directive", with no "Refused to".
        page.Console += (_, m) => { if (m.Text.Contains("Content Security Policy")) refused.Add(m.Text); };

        await OpenTokenTab(page, tester, invoiceId);
        await page.Locator("#LnurlTokenNetworks button[data-network='usdt-arbitrum']").ClickAsync();
        await page.Locator("#LnurlTokenConnect").ClickAsync();
        await Expect(page.Locator("wui-qr-code[uri^='wc:']")).ToBeVisibleAsync(new() { Timeout = 60_000 });

        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator("#LnurlTokenWalletError")).ToContainTextAsync("closed");
        await Expect(page.Locator("#LnurlTokenConnect")).ToBeEnabledAsync();
        Assert.Empty(refused);
    }

    async Task<ServerTester> Start([CallerMemberName] string scope = "")
    {
        var dll = typeof(LNURLVerifyPlugin).Assembly.Location.Replace("\\", "/");
        // ServerTester reads DEBUG_PLUGINS from this file over the environment, so it names this plugin alone (as the Electrum tests do).
        await File.WriteAllTextAsync(Path.Combine(TestUtils.TestDirectory, "appsettings.dev.json"), $"{{\"DEBUG_PLUGINS\":\"{dll}\"}}");
        var tester = CreateServerTester(scope, newDb: true);
        await tester.StartAsync();
        return tester;
    }

    // A store whose Lightning is the stub LNURL, once the provisioner has added LNURL-USDT, and a 0.001 BTC invoice on it.
    static async Task<(TestAccount User, string InvoiceId)> Invoice(ServerTester tester, StubLnurl stub)
    {
        var user = tester.NewAccount();
        await user.GrantAccessAsync();
        var stores = tester.PayTester.GetService<StoreRepository>();
        var store = (await stores.FindStore(user.StoreId))!;
        store.SetPaymentMethodConfig(PaymentMethodId.Parse("BTC-LN"), new JObject { ["connectionString"] = $"type=lnurl;value={stub.PayUrl}" });
        await stores.UpdateStore(store);
        await TestUtils.EventuallyAsync(async () =>
            Assert.NotNull((await stores.FindStore(user.StoreId))!.GetPaymentMethodConfig(TokenAssets.PaymentMethodIdOf("USDT"))));
        var client = await user.CreateClient();
        var invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest { Amount = 0.001m, Currency = "BTC" });
        return (user, invoice.Id);
    }

    static async Task OpenTokenTab(IPage page, ServerTester tester, string invoiceId)
    {
        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "i/" + invoiceId).AbsoluteUri);
        await page.Locator("a.payment-method", new() { HasText = "USDT" }).ClickAsync();
        await Expect(page.Locator("#LnurlTokenNetworks")).ToBeVisibleAsync();
    }
}
