using System.Text.RegularExpressions;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Tests.LNURLVerify;

// Chromium is launched directly, CSP on, as in TokenCheckoutBrowserTests. Needs the BTCPay test stack; see VERIFICATION.md §6.
[Trait("Integration", "Integration")]
public class LightningSetupBrowserTests : UnitTestBase
{
    public LightningSetupBrowserTests(ITestOutputHelper helper) : base(helper) { }

    [Fact(Timeout = 300_000)]
    public async Task A_merchant_looks_up_an_lnurl_and_saves_it_from_the_lightning_address_tab()
    {
        await using var stub = await StubLnurl.Start();
        using var tester = await TokenCheckoutBrowserTests.Start(this);
        var user = tester.NewAccount();
        await user.GrantAccessAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        var refused = new List<string>();
        page.Console += (_, m) => { if (m.Text.Contains("Content Security Policy")) refused.Add(m.Text); };
        var setup = new Uri(tester.PayTester.ServerUri, $"stores/{user.StoreId}/lightning/BTC/setup").AbsoluteUri;
        var summary = page.Locator("#LNURLVerifySummary");

        await page.GotoAsync(new Uri(tester.PayTester.ServerUri, "login").AbsoluteUri);
        await page.FillAsync("#Email", user.RegisterDetails.Email);
        await page.FillAsync("#Password", user.RegisterDetails.Password);
        await page.ClickAsync("#LoginButton");
        await page.WaitForURLAsync(url => !url.Contains("/login"));
        await page.GotoAsync(setup);
        await Expect(page.Locator("#CustomLNURLVerifyHeader")).ToHaveCountAsync(0);
        await page.ClickAsync("label[for='LightningNodeType-LNURLVerify']");
        stub.InvoicePrefix = "lntbs";
        await page.FillAsync("#LNURLVerifyAddress", stub.PayUrl);

        await Expect(summary.Locator("tr[data-option='lightning']")).ToContainTextAsync("Offered", new() { Timeout = 30_000 });
        await Expect(summary.Locator("tr[data-option='usdt-arbitrum']")).ToContainTextAsync("USDT on Arbitrum Sepolia");
        await Expect(summary.Locator("tr[data-option='usdt-tron']")).ToContainTextAsync("Offered");
        await Expect(summary).ToContainTextAsync("checked when you save");
        Assert.Equal(0, stub.Callbacks);

        // Mutinynet-style invoices: only saving's probe can tell, and the refusal shows in the tab.
        await page.ClickAsync("#page-primary");
        var error = page.Locator("#LNURLVerifySetup [data-valmsg-for='ConnectionString']");
        await Expect(error).ToContainTextAsync("Signet", new() { Timeout = 30_000 });
        await Expect(error).ToContainTextAsync("Regtest");
        await Expect(page.Locator("#LightningNodeType-LNURLVerify")).ToBeCheckedAsync();
        await Expect(page.Locator("#LNURLVerifyAddress")).ToHaveValueAsync(stub.PayUrl);
        await Expect(summary.Locator("tr[data-option='lightning']")).ToContainTextAsync("Offered", new() { Timeout = 30_000 });
        Assert.Equal(1, stub.Callbacks);

        stub.InvoicePrefix = "lnbcrt";
        await page.ClickAsync("#page-primary");
        await Expect(page).ToHaveURLAsync(new Regex("/lightning/BTC/settings"), new() { Timeout = 30_000 });
        Assert.Equal(2, stub.Callbacks);
        var store = await tester.PayTester.GetService<StoreRepository>().FindStore(user.StoreId);
        Assert.Equal($"type=lnurl;value={stub.PayUrl}", store!.GetPaymentMethodConfig(PaymentMethodId.Parse("BTC-LN"))?["connectionString"]?.ToString());
        var client = await user.CreateClient();
        var invoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest { Amount = 0.001m, Currency = "BTC" });
        Assert.StartsWith("lnbcrt", (await client.GetInvoicePaymentMethods(invoice.Id)).Single(m => m.PaymentMethodId == "BTC-LN").Destination);

        var beforeReopen = stub.Callbacks;
        await page.GotoAsync(setup);
        await Expect(page.Locator("#LightningNodeType-LNURLVerify")).ToBeCheckedAsync();
        await Expect(page.Locator("#LNURLVerifyAddress")).ToHaveValueAsync(stub.PayUrl);
        await Expect(summary.Locator("tr[data-option='usdt-solana']")).ToContainTextAsync("Offered", new() { Timeout = 30_000 });
        Assert.Equal(beforeReopen, stub.Callbacks);
        Assert.Empty(refused);
    }
}
