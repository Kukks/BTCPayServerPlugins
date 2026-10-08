using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        var (user, invoiceId) = await Invoice(tester, stub.PayUrl, "USDT");
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
        // The prompt's destination is Tron here, the last network the payer opened.
        var paid = Assert.Single((await client.GetInvoicePaymentMethods(invoiceId)).SelectMany(m => m.Payments));
        Assert.Equal(StubLnurl.EvmRecipient, paid.Destination);

        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "login").AbsoluteUri);
        await page.FillAsync("#Email", user.RegisterDetails.Email);
        await page.FillAsync("#Password", user.RegisterDetails.Password);
        await page.ClickAsync("#LoginButton");
        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "invoices/" + invoiceId).AbsoluteUri);
        var row = page.Locator("section:has(> h5:text('LNURL rail payments')) tbody tr");
        await Expect(row).ToContainTextAsync("Arbitrum Sepolia");
        await Expect(row.Locator($"a[href='https://sepolia.arbiscan.io/tx/{txHash}']")).ToHaveCountAsync(1);
    }

    // lnurl-server's FixedFloat rails as its spec and the coordinator's rulings describe them, served by the stub rather than
    // lnurl-server's simulated provider; a stock install's default assets must pick up both USDT and USDC.
    [Fact(Timeout = 600_000)]
    public async Task A_fixedfloat_quote_names_its_provider_requotes_on_expiry_and_settles_on_the_deposit_transaction()
    {
        await using var stub = await StubLnurl.Start();
        stub.QuoteLifetimes["ff-usdtarbitrum"] = TimeSpan.FromSeconds(60);
        using var tester = await Start();
        var (user, invoiceId) = await Invoice(tester, stub.FixedFloatPayUrl, "USDT", "USDC");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var qr = page.Locator(".qr-container");

        await OpenTokenTab(page, tester, invoiceId);
        await Expect(page.Locator("#LnurlTokenNetworks button")).ToHaveTextAsync(new[] { "Arbitrum One via FixedFloat", "Tron via FixedFloat" });
        string Evm(int n) => $"ethereum:{StubLnurl.ArbitrumUsdt}@42161/transfer?address={StubLnurl.DepositAddress("ff-usdtarbitrum", n)}&uint256=63360000";
        await page.Locator("#LnurlTokenNetworks button[data-network='ff-usdtarbitrum']").ClickAsync();
        await Expect(page.Locator("#LnurlTokenAmount")).ToHaveTextAsync("63.36 USDT on Arbitrum One via FixedFloat");
        // Only a tap orders a quote: Tron, open in the same tab but never tapped, has none.
        Assert.Equal((1, 0), (stub.Orders("ff-usdtarbitrum"), stub.Orders("ff-usdttrc")));
        await Expect(qr).ToHaveAttributeAsync("data-qr-value", Evm(1));
        await Expect(page.Locator("#LnurlTokenProvider")).ToHaveTextAsync(
            "This deposit address belongs to FixedFloat, a third-party service, not to the merchant. Send exactly this amount before the " +
            "quote expires: a late, short or excess deposit is resolved with FixedFloat, not with the merchant or BTCPay Server.");
        await Expect(page.Locator("#LnurlTokenExpiry")).ToHaveTextAsync(new Regex(@"^Quote valid for (1:00|0:[0-5]\d)$"));

        await Expect(page.Locator("#LnurlTokenRefresh")).ToHaveTextAsync("Quote expired: get a new one", new() { Timeout = 120_000 });
        stub.QuoteLifetimes.TryRemove("ff-usdtarbitrum", out _);
        await page.Locator("#LnurlTokenRefresh").ClickAsync();
        await Expect(qr).ToHaveAttributeAsync("data-qr-value", Evm(2), new() { Timeout = 60_000 });
        await Expect(page.Locator("#LnurlTokenExpiry")).ToHaveTextAsync(new Regex(@"^Quote valid for (10:00|[5-9]:[0-5]\d)$"));

        await page.Locator("#LnurlTokenNetworks button[data-network='ff-usdttrc']").ClickAsync();
        await Expect(qr).ToHaveAttributeAsync("data-qr-value", StubLnurl.DepositAddress("ff-usdttrc", 1));
        await Expect(page.Locator("#LnurlTokenAmount")).ToHaveTextAsync("63.36 USDT on Tron via FixedFloat");
        await Expect(page.Locator("#LnurlTokenOpenWallet")).ToHaveCountAsync(0);

        var depositTx = "0x" + new string('c', 64);
        stub.Settle("ff-usdtarbitrum/2", depositTx);
        var client = await user.CreateClient();
        await TestUtils.EventuallyAsync(async () =>
            Assert.Equal(InvoiceStatus.Settled, (await client.GetInvoice(invoiceId)).Status), 60_000);
        var paid = Assert.Single((await client.GetInvoicePaymentMethods(invoiceId)).SelectMany(m => m.Payments));
        Assert.Equal(StubLnurl.DepositAddress("ff-usdtarbitrum", 2), paid.Destination);
        Assert.StartsWith(depositTx + ":", paid.Id);
        Assert.True(stub.BatchVerifies > 0);
        Assert.Equal(0, stub.FixedFloatSingleVerifies);

        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "login").AbsoluteUri);
        await page.FillAsync("#Email", user.RegisterDetails.Email);
        await page.FillAsync("#Password", user.RegisterDetails.Password);
        await page.ClickAsync("#LoginButton");
        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "invoices/" + invoiceId).AbsoluteUri);
        var row = page.Locator("section:has(> h5:text('LNURL rail payments')) tbody tr");
        await Expect(row).ToContainTextAsync("Arbitrum One");
        await Expect(row.Locator($"a[href='https://arbiscan.io/tx/{depositTx}']")).ToHaveCountAsync(1);
    }

    [Fact(Timeout = 300_000)]
    public async Task Connect_wallet_opens_a_walletconnect_pairing_under_the_checkout_csp()
    {
        var projectId = Environment.GetEnvironmentVariable("WALLETCONNECT_PROJECT_ID")
                        ?? throw new InvalidOperationException("Set WALLETCONNECT_PROJECT_ID to a Reown project ID (see VERIFICATION.md §6).");
        await using var stub = await StubLnurl.Start();
        using var tester = await Start();
        await tester.PayTester.GetService<ISettingsRepository>().UpdateSetting(new LnurlTokenServerSettings { WalletConnectProjectId = projectId });
        var (_, invoiceId) = await Invoice(tester, stub.PayUrl, "USDT");
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

    // A store whose Lightning is the stub LNURL, once the provisioner has added each asset's payment method, and a 0.001 BTC invoice on it.
    static async Task<(TestAccount User, string InvoiceId)> Invoice(ServerTester tester, string payUrl, params string[] assets)
    {
        var user = tester.NewAccount();
        await user.GrantAccessAsync();
        var stores = tester.PayTester.GetService<StoreRepository>();
        var store = (await stores.FindStore(user.StoreId))!;
        store.SetPaymentMethodConfig(PaymentMethodId.Parse("BTC-LN"), new JObject { ["connectionString"] = $"type=lnurl;value={payUrl}" });
        await stores.UpdateStore(store);
        await TestUtils.EventuallyAsync(async () =>
        {
            var provisioned = (await stores.FindStore(user.StoreId))!;
            Assert.All(assets, a => Assert.NotNull(provisioned.GetPaymentMethodConfig(TokenAssets.PaymentMethodIdOf(a))));
        });
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
