using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Breez.Sdk;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Models;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.Breez;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/Breez")]
public class BreezController : Controller
{
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly BreezService _breezService;
    private readonly BTCPayWalletProvider _btcWalletProvider;

    public BreezController(BTCPayNetworkProvider btcPayNetworkProvider,
        BreezService breezService,
        BTCPayWalletProvider btcWalletProvider)
    {
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _breezService = breezService;
        _btcWalletProvider = btcWalletProvider;
    }


    [HttpGet("")]
    public async Task<IActionResult> Index(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        return RedirectToAction(client is null ? nameof(Configure) : nameof(Info), new {storeId});
    }

    [HttpGet("swapin")]
    public async Task<IActionResult> SwapIn(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpGet("info")]
    public async Task<IActionResult> Info(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpGet("sweep")]
    public async Task<IActionResult> Sweep(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpPost("sweep")]
    public async Task<IActionResult> Sweep(string storeId, string address, uint satPerByte)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        if (address.Equals("store", StringComparison.InvariantCultureIgnoreCase))
        {
            var store = ControllerContext.HttpContext.GetStoreData()
                .GetDerivationSchemeSettings(_btcPayNetworkProvider, "BTC");
            var res = await _btcWalletProvider.GetWallet(storeId)
                .ReserveAddressAsync(storeId, store.AccountDerivation, "Breez");
            address = res.Address.ToString();
        }

        try
        {
            var response = client.Sdk.Sweep(new SweepRequest(address, satPerByte));

            TempData[WellKnownTempData.SuccessMessage] = $"sweep successful: {response.txid}";
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"error with sweep: {e.Message}";
        }


        return View((object) storeId);
    }

    [HttpGet("swapin/create")]
    public async Task<IActionResult> SwapInCreate(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        client.Sdk.ReceiveOnchain(new ReceiveOnchainRequest());
        TempData[WellKnownTempData.SuccessMessage] = "Swapin created successfully";
        return RedirectToAction(nameof(SwapIn), new {storeId});
    }


    [HttpGet("swapout")]
    public async Task<IActionResult> SwapOut(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpPost("swapout")]
    public async Task<IActionResult> SwapOut(string storeId, string address, ulong amount, uint satPerByte,
        string feesHash)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        if (address.Equals("store", StringComparison.InvariantCultureIgnoreCase))
        {
            var store = ControllerContext.HttpContext.GetStoreData()
                .GetDerivationSchemeSettings(_btcPayNetworkProvider, "BTC");
            var res = await _btcWalletProvider.GetWallet(storeId)
                .ReserveAddressAsync(storeId, store.AccountDerivation, "Breez");
            address = res.Address.ToString();
        }

        try
        {
            var result = client.Sdk.SendOnchain(new SendOnchainRequest(amount, address, feesHash, satPerByte));
            TempData[WellKnownTempData.SuccessMessage] = $"swap out created: {result.reverseSwapInfo.id}";
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Couldnt create swap out: {e.Message}";
        }

        return RedirectToAction("SwapOut", new {storeId});
    }

    [HttpGet("swapin/{address}/refund")]
    public async Task<IActionResult> SwapInRefund(string storeId, string address)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }

    [HttpPost("swapin/{address}/refund")]
    public async Task<IActionResult> SwapInRefund(string storeId, string address, string refundAddress, uint satPerByte)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            var resp = client.Sdk.Refund(new RefundRequest(address, refundAddress, satPerByte));
            TempData[WellKnownTempData.SuccessMessage] = $"Refund tx: {resp.refundTxId}";
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Couldnt refund: {e.Message}";
        }

        return RedirectToAction(nameof(SwapIn), new {storeId});
    }

    [HttpGet("configure")]
    public async Task<IActionResult> Configure(string storeId)
    {
        return View(await _breezService.Get(storeId));
    }

    [HttpPost("")]
    public async Task<IActionResult> Configure(string storeId, string command, BreezSettings settings)
    {
        if (command == "clear")
        {
            await _breezService.Set(storeId, null);
            TempData[WellKnownTempData.SuccessMessage] = "Settings cleared successfully";
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        if (command == "save")
        {
            try
            {
                await _breezService.Set(storeId, settings);
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Couldnt use provided settings: {e.Message}";
                return View(settings);
            }

            TempData[WellKnownTempData.SuccessMessage] = "Settings saved successfully";
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return NotFound();
    }

    [Route("payments")]
    public async Task<IActionResult> Payments(string storeId, PaymentsViewModel viewModel)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }
        viewModel ??= new PaymentsViewModel();

        viewModel.Payments = client.Sdk.ListPayments(new ListPaymentsRequest(PaymentTypeFilter.ALL, null, null, null,
            (uint?) viewModel.Skip, (uint?) viewModel.Count));

        return View(viewModel);
    }
}

public class PaymentsViewModel : BasePagingViewModel
{
    public List<Payment> Payments { get; set; } = new();
    public override int CurrentPageCount => Payments.Count;
}