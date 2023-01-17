using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom.Events;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NBitcoin;
using NBitcoin.Payment;
using NBXplorer;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace BTCPayServer.Plugins.Wabisabi
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/Wabisabi")]
    public partial class WabisabiStoreController : Controller
    {
        private readonly WabisabiService _WabisabiService;
        private readonly WalletProvider _walletProvider;
        private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
        private readonly IExplorerClientProvider _explorerClientProvider;
        private readonly WabisabiCoordinatorService _wabisabiCoordinatorService;
        private readonly WabisabiCoordinatorClientInstanceManager _instanceManager;

        public WabisabiStoreController(WabisabiService WabisabiService, WalletProvider walletProvider,
            IBTCPayServerClientFactory btcPayServerClientFactory,
            IExplorerClientProvider explorerClientProvider,
            WabisabiCoordinatorService wabisabiCoordinatorService,
            WabisabiCoordinatorClientInstanceManager instanceManager)
        {
            _WabisabiService = WabisabiService;
            _walletProvider = walletProvider;
            _btcPayServerClientFactory = btcPayServerClientFactory;
            _explorerClientProvider = explorerClientProvider;
            _wabisabiCoordinatorService = wabisabiCoordinatorService;
            _instanceManager = instanceManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateWabisabiStoreSettings(string storeId)
        {
            WabisabiStoreSettings Wabisabi = null;
            try
            {
                Wabisabi = await _WabisabiService.GetWabisabiForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            return View(Wabisabi);
        }


        [HttpPost("")]
        public async Task<IActionResult> UpdateWabisabiStoreSettings(string storeId, WabisabiStoreSettings vm,
            string command)
        {
            var pieces = command.Split(":");
            var actualCommand = pieces[0];
            var commandIndex = pieces.Length > 1 ? pieces[1] : null;
            var coordinator = pieces.Length > 2 ? pieces[2] : null;
            var coord = vm.Settings.SingleOrDefault(settings => settings.Coordinator == coordinator);
            ModelState.Clear();

            WabisabiCoordinatorSettings coordSettings;
            switch (actualCommand)
            {
                case "discover":
                    coordSettings = await _wabisabiCoordinatorService.GetSettings();
                    var relay = commandIndex ??
                                coordSettings?.NostrRelay.ToString();

                    if (Uri.TryCreate(relay, UriKind.Absolute, out var relayUri))
                    {
                        ViewBag.DiscoveredCoordinators =await Nostr.Discover(relayUri,
                            _explorerClientProvider.GetExplorerClient("BTC").Network.NBitcoinNetwork,
                            coordSettings.Key?.CreateXOnlyPubKey().ToHex(), CancellationToken.None);
                    }
                    else
                    {
                        TempData["ErrorMessage"] = $"No relay uri was provided";
                    }

                    return View(vm);
                case "add-coordinator":
                    var name = commandIndex;
                    var uri = coordinator;

                    coordSettings = await _wabisabiCoordinatorService.GetSettings();
                    if (coordSettings.DiscoveredCoordinators.All(discoveredCoordinator =>
                            discoveredCoordinator.Name != name))
                    {
                        coordSettings.DiscoveredCoordinators.Add(new DiscoveredCoordinator() {Name = name,});
                        await _wabisabiCoordinatorService.UpdateSettings(coordSettings);
                        _instanceManager.AddCoordinator($"nostr[{name}]", name, provider => new Uri(uri));

                        TempData["SuccessMessage"] = $"Coordinator {commandIndex} added and started";
                        return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
                    }

                    else
                    {
                        TempData["ErrorMessage"] =
                            $"Coordinator {commandIndex} could not be added because the name was not unique";
                        return View(vm);
                    }

                    break;
                case "remove-coordinator":
                    coordSettings = await _wabisabiCoordinatorService.GetSettings();
                    if (coordSettings.DiscoveredCoordinators.RemoveAll(discoveredCoordinator =>
                            discoveredCoordinator.Name == commandIndex) > 0)
                    {
                        TempData["SuccessMessage"] = $"Coordinator {commandIndex} stopped and removed";
                        await _wabisabiCoordinatorService.UpdateSettings(coordSettings);
                        await _instanceManager.RemoveCoordinator(commandIndex);
                        return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
                    }
                    else
                    {
                        TempData["ErrorMessage"] =
                            $"Coordinator {commandIndex} could not be removed because it was not found";
                    }

                    return View(vm);
                    break;
                case "check":
                    await _walletProvider.Check(storeId, CancellationToken.None);
                    TempData["SuccessMessage"] = "Store wallet re-checked";
                    return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});
                case "exclude-label-add":
                    vm.InputLabelsExcluded.Add("");
                    return View(vm);

                case "exclude-label-remove":
                    vm.InputLabelsExcluded.Remove(commandIndex);
                    return View(vm);
                case "include-label-add":
                    vm.InputLabelsAllowed.Add("");
                    return View(vm);
                case "include-label-remove":
                    vm.InputLabelsAllowed.Remove(commandIndex);
                    return View(vm);

                case "save":
                    foreach (WabisabiStoreCoordinatorSettings settings in vm.Settings)
                    {
                        vm.InputLabelsAllowed = vm.InputLabelsAllowed.Where(s => !string.IsNullOrEmpty(s)).Distinct()
                            .ToList();
                        vm.InputLabelsExcluded = vm.InputLabelsExcluded.Where(s => !string.IsNullOrEmpty(s)).Distinct()
                            .ToList();
                    }

                    await _WabisabiService.SetWabisabiForStore(storeId, vm);
                    TempData["SuccessMessage"] = "Wabisabi settings modified";
                    return RedirectToAction(nameof(UpdateWabisabiStoreSettings), new {storeId});

                default:
                    return View(vm);
            }
        }

        [HttpGet("spend")]
        public async Task<IActionResult> Spend(string storeId)
        {
            if ((await _walletProvider.GetWalletAsync(storeId)) is not BTCPayWallet wallet)
            {
                return NotFound();
            }

            return View(new SpendViewModel() { });
        }

        [HttpPost("spend")]
        public async Task<IActionResult> Spend(string storeId, SpendViewModel spendViewModel, string command)
        {
            if ((await _walletProvider.GetWalletAsync(storeId)) is not BTCPayWallet wallet)
            {
                return NotFound();
            }

            var n = _explorerClientProvider.GetExplorerClient("BTC").Network.NBitcoinNetwork;
            if (string.IsNullOrEmpty(spendViewModel.Destination))
            {
                ModelState.AddModelError(nameof(spendViewModel.Destination),
                    "A destination is required");
            }
            else
            {
                try
                {
                    BitcoinAddress.Create(spendViewModel.Destination, n);
                }
                catch (Exception e)
                {
                    try
                    {
                        new BitcoinUrlBuilder(spendViewModel.Destination, n);
                    }
                    catch (Exception exception)
                    {
                        ModelState.AddModelError(nameof(spendViewModel.Destination),
                            "A destination must be a bitcoin address or a bip21 payment link");
                    }
                }
            }

            if (spendViewModel.Amount is null)
            {
                try
                {
                    spendViewModel.Amount =
                        new BitcoinUrlBuilder(spendViewModel.Destination, n).Amount.ToDecimal(MoneyUnit.BTC);
                }
                catch (Exception e)
                {
                    ModelState.AddModelError(nameof(spendViewModel.Amount),
                        "An amount was not specified and the destination did not have an amount specified");
                }
            }

            if (!ModelState.IsValid)
            {
                return View();
            }

            if (command == "payout")
            {
                var client = await _btcPayServerClientFactory.Create(null, storeId);
                await client.CreatePayout(storeId,
                    new CreatePayoutThroughStoreRequest()
                    {
                        Approved = true, Amount = spendViewModel.Amount, Destination = spendViewModel.Destination
                    });

                TempData["SuccessMessage"] =
                    "The payment has been scheduled. If payment batching is enabled in the coinjoin settings, and the coordinator supports sending that amount and that address type, it will be batched.";

                return RedirectToAction("UpdateWabisabiStoreSettings", new {storeId});
            }

            var coins = await wallet.GetAllCoins();
            if (command == "compute-with-selection")
            {
                if (spendViewModel.SelectedCoins?.Any() is true)
                {
                    coins = (CoinsView)coins.FilterBy(coin =>
                        spendViewModel.SelectedCoins.Contains(coin.Outpoint.ToString()));
                }
            }

            if (command == "compute-with-selection" || command == "compute")
            {
                if (spendViewModel.Amount is null)
                {
                    spendViewModel.Amount =
                        new BitcoinUrlBuilder(spendViewModel.Destination, n).Amount.ToDecimal(MoneyUnit.BTC);
                }

                var defaultCoinSelector = new DefaultCoinSelector();
                var defaultSelection =
                    (defaultCoinSelector.Select(coins.Select(coin => coin.Coin).ToArray(),
                        new Money((decimal)spendViewModel.Amount, MoneyUnit.BTC)) ?? Array.Empty<ICoin>())
                        .ToArray();
                var selector = new SmartCoinSelector(coins.ToList());
                var smartSelection = selector.Select(defaultSelection,
                    new Money((decimal)spendViewModel.Amount, MoneyUnit.BTC));
                spendViewModel.SelectedCoins = smartSelection.Select(coin => coin.Outpoint.ToString()).ToArray();
                return View(spendViewModel);
            }

            if (command == "send")
            {
                var userid = HttpContext.User.Claims.Single(claim => claim.Type == ClaimTypes.NameIdentifier).Value;
                var client = await _btcPayServerClientFactory.Create(userid, storeId);
                var tx = await client.CreateOnChainTransaction(storeId, "BTC",
                    new CreateOnChainTransactionRequest()
                    {
                        SelectedInputs = spendViewModel.SelectedCoins?.Select(OutPoint.Parse).ToList(),
                        Destinations =
                            new List<CreateOnChainTransactionRequest.CreateOnChainTransactionRequestDestination>()
                            {
                                new CreateOnChainTransactionRequest.CreateOnChainTransactionRequestDestination()
                                {
                                    Destination = spendViewModel.Destination, Amount = spendViewModel.Amount
                                }
                            }
                    });

                TempData["SuccessMessage"] =
                    $"The tx {tx.TransactionHash} has been broadcast.";

                return RedirectToAction("UpdateWabisabiStoreSettings", new {storeId});
            }

            return View(spendViewModel);
        }


        public class SpendViewModel
        {
            public string Destination { get; set; }
            public decimal? Amount { get; set; }
            public string[] SelectedCoins { get; set; }
        }
    }
}
