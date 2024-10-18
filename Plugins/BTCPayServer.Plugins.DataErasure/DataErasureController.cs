using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.DataErasure
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/DataErasure")]
    public class DataErasureController : Controller
    {
        private readonly DataErasureService _dataErasureService;

        public DataErasureController(DataErasureService dataErasureService)
        {
            _dataErasureService = dataErasureService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Update(string storeId)
        {
            var vm = await _dataErasureService.Get(storeId) ?? new DataErasureSettings();

            return View(vm);
        }

        [HttpPost("")]
        public async Task<IActionResult> Update(string storeId, DataErasureSettings vm,
            string command)
        {
            if (_dataErasureService.IsRunning)
            {
                TempData["ErrorMessage"] =
                    "Data erasure is currently running and cannot be changed. Please try again later.";
            }


            if (vm.Enabled)
            {
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }
            }


            switch (command)
            {
                case "cleardate":
                    await _dataErasureService.Set(storeId, vm, true);

                    TempData["SuccessMessage"] = "Data erasure settings modified and date cleared";
                    return RedirectToAction(nameof(Update), new {storeId});
                case "save":
                    await _dataErasureService.Set(storeId, vm);
                    TempData["SuccessMessage"] = "Data erasure settings modified";
                    return RedirectToAction(nameof(Update), new {storeId});

                default:
                    return View(vm);
            }
        }
    }
}