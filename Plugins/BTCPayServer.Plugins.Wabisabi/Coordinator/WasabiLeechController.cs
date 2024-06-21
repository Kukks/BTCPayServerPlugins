using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Wabisabi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WalletWasabi.Backend.Controllers;

[Route("plugins/wabisabi-coordinator")]
[AllowAnonymous]
public class WasabiLeechController : Controller
{
    private readonly WabisabiCoordinatorClientInstanceManager _coordinatorClientInstanceManager;
    private readonly WabisabiCoordinatorService _wabisabiCoordinatorService;

    public WasabiLeechController(WabisabiCoordinatorClientInstanceManager coordinatorClientInstanceManager,
        WabisabiCoordinatorService wabisabiCoordinatorService)
    {
        _coordinatorClientInstanceManager = coordinatorClientInstanceManager;
        _wabisabiCoordinatorService = wabisabiCoordinatorService;
    }

    [HttpGet("api/v4/Wasabi/legaldocuments")]
    public async Task<IActionResult> GetLegalDocuments()
    {
        if (_coordinatorClientInstanceManager.HostedServices.TryGetValue("local", out var instance))
        {
            return Ok(instance.TermsConditions);
        }

        return NotFound();
    }

    [Route("{*key}")]
    public async Task<IActionResult> Forward(string key, CancellationToken cancellationToken)
    {
        if (!_wabisabiCoordinatorService.Started)
            return NotFound();

        var settings = await _wabisabiCoordinatorService.GetSettings();

        if (settings.ForwardEndpoint is not null)
        {
            var b = new UriBuilder(settings.ForwardEndpoint)
            {
                Path = key,
                Query = Request.QueryString.ToString()
            };

            return RedirectPreserveMethod(b.ToString());
        }

        return NotFound();
    }
}