using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.Backend.Controllers;

namespace BTCPayServer.Plugins.Wabisabi
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/wabisabi-coordinator/edit")]
    public class WabisabiCoordinatorConfigController : Controller
    {
        private readonly WabisabiCoordinatorService _wabisabiCoordinatorService;
        public WabisabiCoordinatorConfigController(WabisabiCoordinatorService wabisabiCoordinatorService)
        {
            _wabisabiCoordinatorService = wabisabiCoordinatorService;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateWabisabiSettings()
        {
            WabisabiCoordinatorSettings Wabisabi = null;
            try
            {
                Wabisabi = await _wabisabiCoordinatorService.GetSettings();
                
            }
            catch (Exception)
            {
                // ignored
            }

            return View(Wabisabi);
        }


        [HttpPost("")]
        public async Task<IActionResult> UpdateWabisabiSettings(WabisabiCoordinatorSettings vm,
            string command, string config)
        {
            switch (command)
            {
                case "generate-nostr-key":
                    if (ECPrivKey.TryCreate(new ReadOnlySpan<byte>(RandomNumberGenerator.GetBytes(32)), out var key))
                    {
                        
                        TempData["SuccessMessage"] = "Key generated";
                        vm.NostrIdentity = key.ToHex();
                        await _wabisabiCoordinatorService.UpdateSettings( vm);
                        return RedirectToAction(nameof(UpdateWabisabiSettings));
                    }
                    
                    ModelState.AddModelError("NostrIdentity", "key could not be generated");
                    return View(vm);
                case "save":
                    try
                    {
                        _wabisabiCoordinatorService.WabiSabiCoordinator.Config.Update(config, true);
                    }
                    catch (Exception e)
                    {
                        ModelState.AddModelError("config", "config json was invalid");
                        return View(vm);
                    }
                    await _wabisabiCoordinatorService.UpdateSettings( vm);
                    TempData["SuccessMessage"] = "Wabisabi settings modified";
                    return RedirectToAction(nameof(UpdateWabisabiSettings));

                default:
                    return View(vm);
            }
        }
    }
}
