using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SideShift
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/SideShift")]
    public class SideShiftController : Controller
    {
        private readonly BTCPayServerClient _btcPayServerClient;
        private readonly SideShiftService _sideShiftService;

        public SideShiftController(BTCPayServerClient btcPayServerClient, SideShiftService sideShiftService)
        {
            _btcPayServerClient = btcPayServerClient;
            _sideShiftService = sideShiftService;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateSideShiftSettings(string storeId)
        {
            var store = await _btcPayServerClient.GetStore(storeId);

            UpdateSideShiftSettingsViewModel vm = new UpdateSideShiftSettingsViewModel();
            vm.StoreName = store.Name;
            SideShiftSettings SideShift = null;
            try
            {
                SideShift = await _sideShiftService.GetSideShiftForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            SetExistingValues(SideShift, vm);
            return View(vm);
        }

        private void SetExistingValues(SideShiftSettings existing, UpdateSideShiftSettingsViewModel vm)
        {
            if (existing == null)
                return;
            vm.Enabled = existing.Enabled;
        }

        [HttpPost("")]
        public async Task<IActionResult> UpdateSideShiftSettings(string storeId, UpdateSideShiftSettingsViewModel vm,
            string command)
        {
            if (vm.Enabled)
            {
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }
            }

            var sideShiftSettings = new SideShiftSettings()
            {
                Enabled = vm.Enabled,
            };

            switch (command)
            {
                case "save":
                    await _sideShiftService.SetSideShiftForStore(storeId, sideShiftSettings);
                    TempData["SuccessMessage"] = "SideShift settings modified";
                    return RedirectToAction(nameof(UpdateSideShiftSettings), new {storeId});

                default:
                    return View(vm);
            }
        }
    }
}
