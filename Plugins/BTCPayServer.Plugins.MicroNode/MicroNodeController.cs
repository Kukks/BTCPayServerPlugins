using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.MicroNode;

[Route("plugins/micronode")]
public class MicroNodeController : Controller
{
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly MicroNodeService _microNodeService;
    private readonly StoreRepository _storeRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IAuthorizationService _authorizationService;

    public MicroNodeController(
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,MicroNodeService microNodeService, StoreRepository storeRepository,
        BTCPayNetworkProvider networkProvider, IAuthorizationService authorizationService)
    {
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _microNodeService = microNodeService;
        _storeRepository = storeRepository;
        _networkProvider = networkProvider;
        _authorizationService = authorizationService;
    }

    [HttpGet("configure/{storeId}")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Configure(string storeId)
    {
        var result = await _microNodeService.GetStoreSettings(storeId);
        if (result is not null)
        {
            var xy = await _microNodeService.GetMasterSettingsFromKey(result.Key);
            if (xy is not null)
            {
                HttpContext.Items.Add("MasterStoreId", xy.Value.Item2);
            }
        }

        return View(result);
    }


    [HttpPost("configure/{storeId}")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> Configure(string storeId, string command, MicroNodeStoreSettings settings,
        string? masterStoreId)
    {
        var store = HttpContext.GetStoreData();
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
        {
            return NotFound();
        }

        if (masterStoreId == storeId)
        {
            ModelState.AddModelError("masterStoreId", "Master cannot be the same as this store");
            return View(settings);
        }
        var pmi = PaymentTypes.LN.GetPaymentMethodId(network.CryptoCode);
        var existing = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmi, _paymentMethodHandlerDictionary);
        var isSet = settings?.Key is not null;
        settings ??= new MicroNodeStoreSettings();
        settings.Key ??= Guid.NewGuid().ToString();
        var mlc = new MicroLightningClient(null, _microNodeService, network.NBitcoinNetwork,settings.Key);
        var isStoreSetToThisMicro = existing?.GetExternalLightningUrl() == mlc.ToString();

        switch (command)
        {
            case "save":

                if (!ModelState.IsValid)
                {
                    return View(settings);
                }

                if (!isSet && masterStoreId is null)
                {
                    ModelState.AddModelError(nameof(settings.Key), "A master was not selected");
                    return View(settings);
                }

                if (!isSet)
                {
                    var masterSettings = await _microNodeService.GetMasterSettings(masterStoreId);
                    if (masterSettings is null)
                    {
                        ModelState.AddModelError(nameof(settings.Key), "The master is not valid");
                        return View(settings);
                    }

                    if (!masterSettings.Enabled)
                    {
                        ModelState.AddModelError(nameof(settings.Key), "The master is not enabled");
                        return View(settings);
                    }

                    if (masterSettings.AdminOnly &&
                        !(await _authorizationService.AuthorizeAsync(User, Policies.CanModifyServerSettings)).Succeeded)
                    {
                        ModelState.AddModelError(nameof(settings.Key),
                            "The master is admin only and you are not an admin");
                        return View(settings);
                    }

                }


                existing ??= new();
                existing.SetLightningUrl(mlc);
                
                store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[pmi], existing);


                await _microNodeService.Set(storeId, settings, masterStoreId);
                await _storeRepository.UpdateStore(store);
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Severity = StatusMessageModel.StatusSeverity.Success,
                    Message = "MicroNode Set"
                });


                return RedirectToAction(nameof(Configure), new {storeId});
            case "clear":
                var ss = await _microNodeService.GetStoreSettings(storeId);
                if (ss is null)
                {
                    return RedirectToAction(nameof(Configure), new {storeId});
                }

                if (isStoreSetToThisMicro)
                {
                    store.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[pmi], null);
                    await _storeRepository.UpdateStore(store);
                }

                await _microNodeService.SetMaster(storeId, null);
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Severity = StatusMessageModel.StatusSeverity.Success,
                    Message = "MicroNode Cleared"
                });
                break;
        }

        return RedirectToAction(nameof(Configure), new {storeId});
    }


    [HttpGet("configure-master/{storeId}")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> ConfigureMaster(string storeId)
    {
        var result = await _microNodeService.GetMasterSettings(storeId);
        return View(result);
    }

    [HttpPost("configure-master/{storeId}")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> ConfigureMaster(string storeId, string command, MicroNodeSettings settings)
    {
        var store = HttpContext.GetStoreData();
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
        {
            return NotFound();
        }

        if (command == "clear")
        {
            await _microNodeService.SetMaster(storeId, (MicroNodeSettings) null);
            TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = "MicroNode Master Cleared"
            });
            return RedirectToAction(nameof(ConfigureMaster), new {storeId});
        }

        if (!ModelState.IsValid)
        {
            return View(settings);
        }
        await _microNodeService.SetMaster(storeId, settings);
        TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "MicroNode Master Update"
        });
        return RedirectToAction(nameof(ConfigureMaster), new {storeId});

    }
}