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

    public WasabiLeechController(WabisabiCoordinatorClientInstanceManager coordinatorClientInstanceManager)
    {
        _coordinatorClientInstanceManager = coordinatorClientInstanceManager;
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
        if (!_coordinatorClientInstanceManager.HostedServices.TryGetValue("zksnacks", out var coordinator))
            return BadRequest();


        var b = new UriBuilder(coordinator.Coordinator);
        b.Path = key;
        b.Query = Request.QueryString.ToString();

        return RedirectPreserveMethod(b.ToString());
    }
}
