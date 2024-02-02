using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Bringin;

public class BringinPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=1.12.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<BringinService>();
        applicationBuilder.AddSingleton<IHostedService, BringinService>(s => s.GetService<BringinService>());
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Bringin/BringinDashboardWidget", "dashboard"));
        applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Bringin/Nav",
            "store-integrations-nav"));
    }
}

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/Bringin")]
public class BringinController : Controller
{
    private readonly BringinService _bringinService;

    public BringinController(BringinService bringinService)
    {
        _bringinService = bringinService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Edit()
    {
        return View();
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string storeId, string apiKey)
    {
        //truncate with showing only first 3 letters on start ond end

        var truncatedApikey = apiKey.Substring(0, 3) + "***" + apiKey.Substring(apiKey.Length - 3);

        return View("Confirm",
            new ConfirmModel("Confirm Bringin API Key",
                $"You are about to set your Bringin API key to {truncatedApikey}", "Set", "btn-primary"));
    }

    [HttpPost("callback")]
    public async Task<IActionResult> CallbackConfirm(string storeId, string apiKey)
    {
        var vm = await _bringinService.Update(storeId);
        vm.ApiKey = apiKey;
        await _bringinService.Update(storeId, vm);
        return RedirectToAction("Edit", new {storeId});
    }
}