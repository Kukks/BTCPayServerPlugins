#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Prism;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("stores/{storeId}/plugins/prism")]
[ContentSecurityPolicy(CSPTemplate.AntiXSS, UnsafeInline = true)]
public class PrismController : Controller
{
    [HttpGet]
    public async Task<IActionResult> Edit()
    {
        return View();
    }
}