using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NicolasDorier.RateLimits;
using Org.BouncyCastle.Security.Certificates;

namespace BTCPayServer.Plugins.DynamicRateLimits
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("~/plugins/dynamicrateslimiter")]
    public class DynamicRatesLimiterController : Controller
    {
        private readonly DynamicRateLimitsService _dynamicRateLimitsService;

        public DynamicRatesLimiterController(DynamicRateLimitsService dynamicRateLimitsService)
        {
            _dynamicRateLimitsService = dynamicRateLimitsService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Update()
        {
            return View(await _dynamicRateLimitsService.Get());
        }


        [HttpPost("")]
        public async Task<IActionResult> Update(DynamicRateLimitSettings vm, string command)
        {
            switch (command)
            {
                case "save":
                    if (vm.RateLimits is not null)
                    {
                        for (int i = 0; i < vm.RateLimits.Length; i++)
                        {
                            if (!LimitRequestZone.TryParse(vm.RateLimits[i], out var zone))
                            {
                                vm.AddModelError(s => s.RateLimits[i], "Invalid rate limit", this);
                            }
                        }
                    }

                    if (!ModelState.IsValid)
                    {
                        return View(vm);
                    }

                    await _dynamicRateLimitsService.Update(vm.RateLimits);
                    TempData["SuccessMessage"] = "Dynamic rate limits modified";
                    return RedirectToAction(nameof(Update));
                case "use-defaults":
                    await _dynamicRateLimitsService.UseDefaults();
                    TempData["SuccessMessage"] = "Dynamic rate limits modified";
                    return RedirectToAction(nameof(Update));
                default:
                    return View(await _dynamicRateLimitsService.Get());
            }
        }
    }
}