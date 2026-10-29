using Microsoft.Playwright;
using Xunit;

namespace BTCPayServer.Tests.LNURLVerify;

// The card's script and stylesheet alone on a blank page, so no BTCPay test stack is needed.
public class SetupCardTests
{
    static string Resource(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../Plugins/BTCPayServer.Plugins.LNURLVerify/Resources/lnurlverify", name));

    [Fact(Timeout = 60_000)]
    public async Task Networks_every_token_offers_lead_each_row_in_one_order_and_line_up()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<div id='out'></div>");
        await page.AddStyleTagAsync(new() { Path = Resource("setup-card.css") });
        await page.AddScriptTagAsync(new() { Path = Resource("setup-card.js") });
        await page.EvaluateAsync("""
            () => {
                const token = (unit, chains) => chains.map(c => ({
                    id: unit + '-' + c, type: unit, unit, toggle: unit + '-LNURL', asset: c + ':1/erc20:x', icon: c, label: unit + ' on ' + c }));
                LnurlSetupCard.render(document.getElementById('out'), {
                    domain: 'x', minSendable: 1000, maxSendable: 2000,
                    options: [...token('USDT', ['arbitrum', 'tron', 'ethereum', 'polygon']), ...token('USDC', ['base', 'polygon', 'arbitrum', 'ethereum'])]
                }, { iconUrl: '{0}.svg' });
            }
            """);

        Assert.Equal(new[] { "USDT-arbitrum", "USDT-ethereum", "USDT-polygon", "USDT-tron" }, await Options(page, "USDT"));
        Assert.Equal(new[] { "USDC-arbitrum", "USDC-ethereum", "USDC-polygon", "USDC-base" }, await Options(page, "USDC"));
        foreach (var chain in new[] { "arbitrum", "ethereum", "polygon" })
            Assert.Equal(await Left(page, "USDT-" + chain), await Left(page, "USDC-" + chain));
    }

    static Task<string[]> Options(IPage page, string unit) =>
        page.Locator($"#LNURLVerifyUnit-{unit} [data-option]").EvaluateAllAsync<string[]>("els => els.map(e => e.dataset.option)");

    static async Task<float> Left(IPage page, string option) =>
        (await page.Locator($"[data-option='{option}']").BoundingBoxAsync())!.X;
}
