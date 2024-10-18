using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.Backend.Controllers;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace BTCPayServer.Plugins.Wabisabi
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/wabisabi-coordinator/edit")]
    public class WabisabiCoordinatorConfigController : Controller
    {
        public static string OurDisclaimer = @"By using this plugin, the user agrees that they are solely responsible for determining the legality of its operation in their jurisdiction and for the handling of any revenue generated through its use. The user also agrees that any coordinator fees generated through the use of the plugin may be configured to be split up and forwarded to multiple destinations, which by default are non-profit organizations. The user also acknowledges that the plugin is open-source and licensed under the MIT license and therefore has the right to adapt the code . The user agrees to use the plugin at their own risk and acknowledges that being a coinjoin coordinator carries certain risks, including but not limited to legal risks, technical risks, privacy risks and reputational risks. It is their responsibility to carefully consider these risks before using the plugin.

This disclaimer serves as a binding agreement between the user and the developers of this plugin and supersedes any previous agreements or understandings, whether written or oral. The user agrees to fully waive and release the developers of this plugin and BTCPay Server contributors, from any and all liabilities, claims, demands, damages, or causes of action arising out of or related to the use of this plugin. In the event of any legal issues arising from the use of this plugin, the user also agrees to indemnify and hold harmless the developers of this plugin and BTCPay Server contributors from any claims, costs, losses, damages, liabilities, judgments and expenses (including reasonable fees of attorneys and other professionals) arising from or in any way related to the user's use of the plugin or violation of these terms. Any failure or delay by the developer to exercise or enforce any right or remedy provided under this disclaimer will not constitute a waiver of that or any other right or remedy, and no single or partial exercise of any right or remedy will preclude or restrict the further exercise of that or any other right or remedy.

Legal risks: as the coordinator, the user may be considered to be operating a money transmitting business and may be subject to regulatory requirements and oversight.

Technical risks: the plugin uses complex cryptography and code, and there may be bugs or vulnerabilities that could result in the loss of funds.

Privacy risks: as the coordinator, the user may have access to sensitive transaction data, and it is their responsibility to protect this data and comply with any applicable privacy laws.

Reputation risks: as the coordinator, the user may be associated with illegal activities and may face reputational damage.";
        private readonly WabisabiCoordinatorService _wabisabiCoordinatorService;
        private readonly BTCPayNetworkProvider _networkProvider;

        public WabisabiCoordinatorConfigController(WabisabiCoordinatorService wabisabiCoordinatorService, BTCPayNetworkProvider  networkProvider)
        {
            _wabisabiCoordinatorService = wabisabiCoordinatorService;
            _networkProvider = networkProvider;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateWabisabiSettings()
        {
            WabisabiCoordinatorSettings Wabisabi = null;
            try
            {
                Wabisabi = await _wabisabiCoordinatorService.GetSettings();
                
                ViewBag.Config = _wabisabiCoordinatorService.WabiSabiCoordinator.Config.ToString();
                
            }
            catch (Exception)
            {
                // ignored
            }

            return View(Wabisabi);
        }

        private static bool IsLocalNetwork(string server)
        {
            ArgumentNullException.ThrowIfNull(server);
            if (Uri.CheckHostName(server) == UriHostNameType.Dns)
            {
                return server.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                       server.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                       server.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
                       server.IndexOf('.', StringComparison.OrdinalIgnoreCase) == -1;
            }
            if (IPAddress.TryParse(server, out var ip))
            {
                return ip.IsLocal() || ip.IsRFC1918();
            }

            if (Uri.TryCreate(server, UriKind.Absolute, out var res) && res.IsLoopback || res.Host == "localhost")
            {
                return true;
            }
            return false;
        }
        [HttpPost("")]
        public async Task<IActionResult> UpdateWabisabiSettings(WabisabiCoordinatorSettings vm,
            string command, string config)
        {
            ViewBag.Config = config;
            switch (command)
            {
                case "nostr-current-url":
                    if (_networkProvider.NetworkType != ChainName.Regtest && IsLocalNetwork(Request.GetAbsoluteRoot()))
                    {
                        TempData["ErrorMessage"] = "the current url is only reachable from your local network. You need a public domain or use Tor.";
                        return View(vm);
                    }
                    else
                    {
                        vm.UriToAdvertise =  new Uri( Request.GetAbsoluteRootUri() + "plugins/wabisabi-coordinator");
                        TempData["SuccessMessage"] = $"Will create nostr events that point to { vm.UriToAdvertise }";
                        await _wabisabiCoordinatorService.UpdateSettings( vm);
                        return RedirectToAction(nameof(UpdateWabisabiSettings));
                    }
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
                        ModelState.AddModelError("config", $"config json was invalid ({e.Message})");
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
