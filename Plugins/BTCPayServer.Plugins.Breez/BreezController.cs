using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Breez.Sdk;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Lightning;
using BTCPayServer.Models;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.DataEncoders;
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

    [HttpGet("send")]
    public async Task<IActionResult> Send(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }   
    [Route("receive")]
    public async Task<IActionResult> Receive(string storeId, ulong? amount)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }
        if (amount is not null)
        {
           var invoice  = await  client.CreateInvoice(LightMoney.FromUnit(amount.Value, LightMoneyUnit.Satoshi).MilliSatoshi, null, TimeSpan.Zero);
           TempData["bolt11"] = invoice.BOLT11;
           return RedirectToAction("Payments", "Breez", new {storeId });
        }
      

        return View((object) storeId);
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send(string storeId, string address, ulong? amount)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        var payParams = new PayInvoiceParams();
        string bolt11 = null;
        if (HexEncoder.IsWellFormed(address))
        {
            if (PubKey.TryCreatePubKey(ConvertHelper.FromHexString(address), out var pubKey))
            {
                if (amount is null)
                {
                    TempData[WellKnownTempData.ErrorMessage] =
                        $"Cannot do keysend payment without specifying an amount";
                    return RedirectToAction(nameof(Send), new {storeId});
                }

                payParams.Amount = amount.Value * 1000;
                payParams.Destination = pubKey;
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = $"invalid nodeid";
                return RedirectToAction(nameof(Send), new {storeId});
            }
        }
        else
        {
            bolt11 = address;
            if (amount is not null)
            {
                payParams.Amount = amount.Value * 1000;
            }
        }

        var result = await client.Pay(bolt11, payParams);

        switch (result.Result)
        {
            case PayResult.Ok:

                TempData[WellKnownTempData.SuccessMessage] = $"Sending successful";
                break;
            case PayResult.Unknown:
            case PayResult.CouldNotFindRoute:
            case PayResult.Error:
            default:

                TempData[WellKnownTempData.ErrorMessage] = $"Sending did not indicate success";
                break;
        }

        return RedirectToAction(nameof(Payments), new {storeId});
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

    [HttpPost("configure")]
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

        viewModel.Payments = client.Sdk.ListPayments(new ListPaymentsRequest(PaymentTypeFilter.ALL, null, null, true,
            (uint?) viewModel.Skip, (uint?) viewModel.Count));

        return View(viewModel);
    }
}

public class PaymentsViewModel : BasePagingViewModel
{
    public List<Payment> Payments { get; set; } = new();
    public override int CurrentPageCount => Payments.Count;
}