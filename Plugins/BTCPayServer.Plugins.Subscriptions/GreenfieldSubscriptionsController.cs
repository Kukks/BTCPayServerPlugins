using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Subscriptions;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldSubscriptionsController : ControllerBase
{
    private readonly AppService _appService;

    public GreenfieldSubscriptionsController(AppService appService)
    {
        _appService = appService;
    }
    
    [HttpGet("~/api/v1/apps/subscriptions/{appId}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetSubscription(string appId)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, includeArchived: true);
        if (app == null)
        {
            return AppNotFound();
        }

        var ss = app.GetSettings<SubscriptionAppSettings>();
        return Ok(ss);
    }    
    [HttpGet("~/api/v1/apps/subscriptions/{appId}/{subscriptionId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSubscriptionId(string appId, string subscriptionId)
    {
        var app = await _appService.GetApp(appId, SubscriptionApp.AppType, includeArchived: true);
        if (app == null)
        {
            return AppNotFound();
        }

        var ss = app.GetSettings<SubscriptionAppSettings>();
        if (!ss.Subscriptions.TryGetValue(subscriptionId, out var subscriber))
        {
            return this.CreateAPIError(404, "subscription-not-found", "The subscription with specified ID was not found");
        }
        return Ok(subscriber);
    }

    private IActionResult AppNotFound()
    {
        return this.CreateAPIError(404, "app-not-found", "The app with specified ID was not found");
    }
}