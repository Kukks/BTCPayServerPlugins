using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.FixedFloat
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/FixedFloat")]
    public class FixedFloatController : Controller
    {
        private readonly FixedFloatService _FixedFloatService;

        public FixedFloatController(FixedFloatService FixedFloatService)
        {
            _FixedFloatService = FixedFloatService;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateFixedFloatSettings(string storeId)
        {
            FixedFloatSettings settings = null;
            try
            {
                settings = await _FixedFloatService.GetFixedFloatForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            return View(settings);
        }

        [HttpPost("")]
        public async Task<IActionResult> UpdateFixedFloatSettings(string storeId, FixedFloatSettings vm,
            string command)
        {
            if (vm.Enabled)
            {
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }
            }

            switch (command)
            {
                case "save":
                    await _FixedFloatService.SetFixedFloatForStore(storeId, vm);
                    TempData["SuccessMessage"] = "FixedFloat settings modified";
                    return RedirectToAction(nameof(UpdateFixedFloatSettings), new {storeId});

                default:
                    return View(vm);
            }
        }
    }
}