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
        private readonly BTCPayServerClient _btcPayServerClient;
        private readonly FixedFloatService _FixedFloatService;

        public FixedFloatController(BTCPayServerClient btcPayServerClient, FixedFloatService FixedFloatService)
        {
            _btcPayServerClient = btcPayServerClient;
            _FixedFloatService = FixedFloatService;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateFixedFloatSettings(string storeId)
        {
            var store = await _btcPayServerClient.GetStore(storeId);

            UpdateFixedFloatSettingsViewModel vm = new UpdateFixedFloatSettingsViewModel();
            vm.StoreName = store.Name;
            FixedFloatSettings FixedFloat = null;
            try
            {
                FixedFloat = await _FixedFloatService.GetFixedFloatForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            SetExistingValues(FixedFloat, vm);
            return View(vm);
        }

        private void SetExistingValues(FixedFloatSettings existing, UpdateFixedFloatSettingsViewModel vm)
        {
            if (existing == null)
                return;
            vm.Enabled = existing.Enabled;
        }

        [HttpPost("")]
        public async Task<IActionResult> UpdateFixedFloatSettings(string storeId, UpdateFixedFloatSettingsViewModel vm,
            string command)
        {
            if (vm.Enabled)
            {
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }
            }

            var FixedFloatSettings = new FixedFloatSettings()
            {
                Enabled = vm.Enabled,
            };

            switch (command)
            {
                case "save":
                    await _FixedFloatService.SetFixedFloatForStore(storeId, FixedFloatSettings);
                    TempData["SuccessMessage"] = "FixedFloat settings modified";
                    return RedirectToAction(nameof(UpdateFixedFloatSettings), new {storeId});

                default:
                    return View(vm);
            }
        }
    }
}
