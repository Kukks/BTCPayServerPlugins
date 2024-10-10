using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Breez.Sdk;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Breez;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/Breez")]
public class BreezController : Controller
{
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly BreezService _breezService;
    private readonly BTCPayWalletProvider _btcWalletProvider;
    private readonly StoreRepository _storeRepository;

    public BreezController(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        BTCPayNetworkProvider btcPayNetworkProvider,
        BreezService breezService,
        BTCPayWalletProvider btcWalletProvider, StoreRepository storeRepository)
    {
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _breezService = breezService;
        _btcWalletProvider = btcWalletProvider;
        _storeRepository = storeRepository;
    }


    [HttpGet("")]
    public async Task<IActionResult> Index(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        return RedirectToAction(client is null ? nameof(Configure) : nameof(Info), new {storeId});
    }

    [HttpGet("swapin")]
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Info(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }
    [HttpGet("logs")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Logs(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View( client.Events);
    }

    [HttpGet("sweep")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
                .GetDerivationSchemeSettings(_paymentMethodHandlerDictionary, "BTC");
            var res = await _btcWalletProvider.GetWallet(storeId)
                .ReserveAddressAsync(storeId, store.AccountDerivation, "Breez");
            address = res.Address.ToString();
        }

        try
        {
            var response = client.Sdk.RedeemOnchainFunds(new RedeemOnchainFundsRequest(address, satPerByte));

            TempData[WellKnownTempData.SuccessMessage] = $"sweep successful: {response.txid}";
        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"error with sweep: {e.Message}";
        }


        return View((object) storeId);
    }

    [HttpGet("send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Send(string storeId)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        return View((object) storeId);
    }   
    [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("receive")]
    public async Task<IActionResult> Receive(string storeId, ulong? amount)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        try
        {
            if (amount is not null)
            {
                var invoice  = await  client.CreateInvoice(LightMoney.FromUnit(amount.Value, LightMoneyUnit.Satoshi).MilliSatoshi, null, TimeSpan.Zero);
                TempData["bolt11"] = invoice.BOLT11;
                return RedirectToAction("Payments", "Breez", new {storeId });
            }

        }
        catch (Exception e)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"{e.Message}";
        }
      

        return View((object) storeId);
    }

    [HttpPost("send")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
                .GetDerivationSchemeSettings(_paymentMethodHandlerDictionary, "BTC");
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
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
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

    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [HttpGet("configure")]
    public async Task<IActionResult> Configure(string storeId)
    {
        return View(await _breezService.Get(storeId));
    }
    
    private static async Task<byte[]> ReadAsByteArrayAsync( Stream source)
    {
        // Optimization
        if (source is MemoryStream memorySource)
            return memorySource.ToArray();

        using var memoryStream = new MemoryStream();
        await source.CopyToAsync(memoryStream);
        return memoryStream.ToArray();
    }

    [HttpPost("configure")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Configure(string storeId, string command, BreezSettings settings)
    {
        var store = HttpContext.GetStoreData();
        var pmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var existing = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmi, _paymentMethodHandlerDictionary);
       
        if (command == "clear")
        {
            await _breezService.Set(storeId, null);
            TempData[WellKnownTempData.SuccessMessage] = "Settings cleared successfully";
            var client = _breezService.GetClient(storeId);
            var isStoreSetToThisMicro = existing?.GetExternalLightningUrl() == client?.ToString();
            if (client is not null && isStoreSetToThisMicro)
            {
                store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[pmi], null);
                await _storeRepository.UpdateStore(store);
            }
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        if (command == "save")
        {
          
            try
            {
                if (string.IsNullOrEmpty(settings.Mnemonic))
                {
                    ModelState.AddModelError(nameof(settings.Mnemonic), "Mnemonic is required");
                    return View(settings);
                }
                else
                 {
                    try
                    {
                        new Mnemonic(settings.Mnemonic);
                    }
                    catch (Exception e)
                    {
                        ModelState.AddModelError(nameof(settings.Mnemonic), "Invalid mnemonic");
                        return View(settings);
                    }
                }

                if (settings.GreenlightCredentials is not null)
                {
                    await using var stream = settings.GreenlightCredentials .OpenReadStream();
                    using var archive = new ZipArchive(stream);
                    var deviceClientArchiveEntry = archive.GetEntry("client.crt");
                    var deviceKeyArchiveEntry = archive.GetEntry("client-key.pem");
                    if(deviceClientArchiveEntry is null || deviceKeyArchiveEntry is null)
                    {
                       ModelState.AddModelError(nameof(settings.GreenlightCredentials), "Invalid zip file (does not have client.crt or client-key.pem)");
                       return View(settings);
                    }
                    else
                    {
                        var deviceClient = await ReadAsByteArrayAsync(deviceClientArchiveEntry.Open());
                        var deviceKey = await ReadAsByteArrayAsync(deviceKeyArchiveEntry.Open());
                        var dir = _breezService.GetWorkDir(storeId);
                        Directory.CreateDirectory(dir);
                        await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, "client.crt"), deviceClient);
                        await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, "client-key.pem"), deviceKey);
                        
                        await _breezService.Set(storeId, settings);
                    }
                    
                }
                else
                {
                    
                    await _breezService.Set(storeId, settings);
                }
            }
            catch (Exception e)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Couldnt use provided settings: {e.Message}";
                return View(settings);
            }

            if(existing is null)
            {

                existing = new LightningPaymentMethodConfig();
                var client = _breezService.GetClient(storeId);
                existing.SetLightningUrl(client);
                store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[pmi], existing);
                var lnurlPMI = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
                store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[lnurlPMI], new LNURLPaymentMethodConfig());
                await _storeRepository.UpdateStore(store);
            }
            
            TempData[WellKnownTempData.SuccessMessage] = "Settings saved successfully";
            return RedirectToAction(nameof(Info), new {storeId});
        }

        return NotFound();
    }

    [Route("payments")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Payments(string storeId, PaymentsViewModel viewModel)
    {
        var client = _breezService.GetClient(storeId);
        if (client is null)
        {
            return RedirectToAction(nameof(Configure), new {storeId});
        }

        viewModel ??= new PaymentsViewModel();
        viewModel.Payments = client.Sdk.ListPayments(new ListPaymentsRequest(null, null, null,null,true,
            (uint?) viewModel.Skip, (uint?) viewModel.Count));

        return View(viewModel);
    }
}

public class PaymentsViewModel : BasePagingViewModel
{
    public List<Payment> Payments { get; set; } = new();
    public override int CurrentPageCount => Payments.Count;
}