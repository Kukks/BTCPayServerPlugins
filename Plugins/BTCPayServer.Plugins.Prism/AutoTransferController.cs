#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Plugins.Prism.Services;
using BTCPayServer.Plugins.Prism.ViewModel;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Prism;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("stores/{storeId}/plugins/auto-transfer/")]
public class AutoTransferController : Controller
{
    private readonly AppService _appService;
    private readonly PayoutMethodHandlerDictionary _handlers;
    private readonly AutoTransferService _autoTransferService;
    private readonly UserManager<ApplicationUser> _userManager;
    public AutoTransferController(PayoutMethodHandlerDictionary handlers, AutoTransferService autoTransferService,
        AppService appService, UserManager<ApplicationUser> userManager)
    {
        _handlers = handlers;
        _appService = appService;
        _userManager = userManager;
        _autoTransferService = autoTransferService;
    }
    public Data.StoreData CurrentStore => HttpContext.GetStoreData();

    private string GetUserId() => _userManager.GetUserId(User);

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (CurrentStore is null)
            return NotFound();

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        var viewModel = new AutoTransferSettingsViewModel
        {
            PendingPayouts = autoTransferSettings.PendingPayouts,
            AutomationTransferDays = autoTransferSettings.AutomationTransferDays, 
            EnableScheduledAutomation = autoTransferSettings.EnableScheduledAutomation,
            Enabled = autoTransferSettings.Enabled,
            SatThreshold = autoTransferSettings.SatThreshold,
            ReserveFeePercentage = autoTransferSettings.Reserve
        };
        return View(viewModel);
    }

    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings(string storeId, AutoTransferSettingsViewModel vm)
    {
        if (CurrentStore is null)
            return NotFound();

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        autoTransferSettings.Enabled = vm.Enabled;
        autoTransferSettings.SatThreshold = vm.SatThreshold;
        autoTransferSettings.Reserve = vm.ReserveFeePercentage;
        autoTransferSettings.EnableScheduledAutomation = vm.EnableScheduledAutomation;
        autoTransferSettings.AutomationTransferDays = vm.AutomationTransferDays;
        await _autoTransferService.UpdateAutoTransferSettingsForStore(CurrentStore.Id, autoTransferSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Auto transfer settings updated successfully.";
        return RedirectToAction(nameof(Index), new { storeId = CurrentStore.Id });
    }


    [HttpGet("pos/configure")]
    public async Task<IActionResult> ConfigurePoSAutoTransfer()
    {
        if (CurrentStore is null)
            return NotFound();

        var user = _userManager.Users.Where(c => c.Id == GetUserId())
                .Include(user => user.UserStores).ThenInclude(data => data.StoreData).SingleOrDefault();

        var posApps = await _appService.GetApps(PointOfSaleAppType.AppType);
        if (posApps == null || !posApps.Any())
        {
            TempData[WellKnownTempData.ErrorMessage] = "There are currently no POS app available for this store";
            return RedirectToAction(nameof(Index), new { storeId = CurrentStore.Id });
        }
        var storePosApps = posApps.Where(c => c.StoreDataId == CurrentStore.Id && !c.Archived).ToList();
        if (storePosApps == null || !storePosApps.Any())
        {
            TempData[WellKnownTempData.ErrorMessage] = "There are currently no POS app available for this store";
            return RedirectToAction(nameof(Index), new { storeId = CurrentStore.Id });
        }

        var userStores = user?.UserStores.Where(s => s.StoreDataId != CurrentStore.Id && !s.StoreData.Archived).Select(s => s.StoreData).ToList() ?? new List<Data.StoreData>();
        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        var savedSplits = autoTransferSettings?.PosProductAutoTransferSplit ?? new List<PosAppProductSplitModel>();
        var model = storePosApps.Select(app =>
        {
            var settings = app.GetSettings<PointOfSaleSettings>();
            var products = new List<ProductSplitItemModel>();
            if (!string.IsNullOrEmpty(settings?.Template))
            {
                var templateItems = JsonSerializer.Deserialize<List<PoSAppItem>>(settings.Template);
                if (templateItems != null)
                {
                    products = templateItems.Select(item =>
                    {
                        var savedProduct = savedSplits.FirstOrDefault(x => x.AppId == app.Id)?.Products.FirstOrDefault(p => p.ProductId == item.Id);
                        var destinationStoreId = savedProduct?.DestinationStoreId;
                        return new ProductSplitItemModel
                        {
                            ProductId = item.Id,
                            Title = item.Title,
                            Price = item.Price ?? 0m,
                            Percentage = savedProduct?.Percentage ?? 0,
                            StoreOptions = userStores.Select(s => new SelectListItem
                            {
                                Value = s.Id,
                                Text = s.StoreName,
                                Selected = (s.Id == destinationStoreId)
                            }).ToList()
                        };
                    }).ToList();
                }
            }
            return new PosAppProductSplitModel
            {
                AppId = app.Id,
                AppTitle = app.Name,
                Products = products
            };
        }).ToList();
        return View(model);
    }

    [HttpPost("pos/configure/save")]
    public async Task<IActionResult> SavePoSAutoTransferSetting(string storeId, List<PosAppProductSplitModel> vm)
    {
        if (CurrentStore is null)
            return NotFound();

        foreach (var item in vm)
        {
            var invalidProducts = item.Products.Where(p => p.Percentage > 0 && string.IsNullOrWhiteSpace(p.DestinationStoreId)).ToList();
            if (invalidProducts.Any())
            {
                TempData[WellKnownTempData.ErrorMessage] = "Products with percentage greater than 0 must have a specified destination store";
                return RedirectToAction(nameof(ConfigurePoSAutoTransfer), new { storeId = CurrentStore.Id });
            }
        }
        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        autoTransferSettings.PosProductAutoTransferSplit = vm;
        await _autoTransferService.UpdateAutoTransferSettingsForStore(CurrentStore.Id, autoTransferSettings);
        TempData[WellKnownTempData.SuccessMessage] = "PoS products configuration updated successfully.";
        return RedirectToAction(nameof(ConfigurePoSAutoTransfer), new { storeId = CurrentStore.Id });
    }


    [HttpGet("send-now")]
    public async Task<IActionResult> ManualTransfer()
    {
        if (CurrentStore is null)
            return NotFound();

        var user = _userManager.Users.Where(c => c.Id == GetUserId())
                .Include(user => user.UserStores).ThenInclude(data => data.StoreData).SingleOrDefault();

        var viewModel = new AutoTransferSettingsViewModel
        {
            AvailableStores = user?.UserStores.Where(s => s.StoreDataId != CurrentStore.Id).Select(s => new SelectListItem
            {
                Value = s.StoreDataId,
                Text = s.StoreData.StoreName
            }).ToList()
        };
        return View(viewModel);
    }

    [HttpPost("process-payment")]
    public async Task<IActionResult> ProcessManualAutoPayment(string storeId, AutoTransferSettingsViewModel vm)
    {
        if (CurrentStore is null)
            return NotFound();

        if (vm.Destinations == null || !vm.Destinations.Any() || vm.Destinations.Any(d => HasNullProperties(d)))
        {
            TempData[WellKnownTempData.ErrorMessage] = "destination fields are required.";
            return RedirectToAction(nameof(ManualTransfer), new { storeId = CurrentStore.Id });
        }

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        if (!autoTransferSettings.Enabled)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Enable auto-transfer to process payment";
            return RedirectToAction(nameof(ManualTransfer), new { storeId = CurrentStore.Id });
        }
        await _autoTransferService.CreatePayouts(storeId, vm);

        return RedirectToAction(nameof(Index), new { storeId = CurrentStore.Id });
    }

    [HttpGet("scheduled-transfer/list")]
    public async Task<IActionResult> ListScheduleTransfer()
    {
        if (CurrentStore is null)
            return NotFound();

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        return View(autoTransferSettings);
    }


    [HttpGet("view-schedule")]
    public async Task<IActionResult> ViewScheduledAutoTransfer(string storeId, string batchId)
    {
        if (CurrentStore is null)
            return NotFound();

        var user = _userManager.Users.Where(c => c.Id == GetUserId())
                .Include(user => user.UserStores).ThenInclude(data => data.StoreData).SingleOrDefault();

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        var viewModel = new AutoTransferSettingsViewModel
        {
            AvailableStores = user?.UserStores.Where(s => s.StoreDataId != CurrentStore.Id).Select(s => new SelectListItem
            {
                Value = s.StoreDataId,
                Text = s.StoreData.StoreName
            }).ToList()
        };
        if (!string.IsNullOrEmpty(batchId) && autoTransferSettings.ScheduledDestinations.ContainsKey(batchId))
        {
            viewModel.Destinations = autoTransferSettings.ScheduledDestinations[batchId];
            viewModel.DestinationBatchId = batchId;
        }
        return View(viewModel);
    }

    [HttpPost("update-schedule/{batchId}")]
    public async Task<IActionResult> UpdateScheduledAutoPayment(string storeId, string batchId, AutoTransferSettingsViewModel vm)
    {
        if (CurrentStore is null)
            return NotFound();

        if (vm.Destinations == null || !vm.Destinations.Any() || vm.Destinations.Any(d => HasNullProperties(d)))
        {
            TempData[WellKnownTempData.ErrorMessage] = "destination fields are required.";
            return RedirectToAction(nameof(ViewScheduledAutoTransfer), new { storeId = CurrentStore.Id, batchId });
        }

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        if (!autoTransferSettings.ScheduledDestinations.ContainsKey(batchId))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Invalid batch schedule.";
            return RedirectToAction(nameof(ViewScheduledAutoTransfer), new { storeId = CurrentStore.Id, batchId });
        }
        autoTransferSettings.ScheduledDestinations ??= new Dictionary<string, List<AutoTransferDestination>>();
        autoTransferSettings.ScheduledDestinations[batchId] = new List<AutoTransferDestination>(vm.Destinations);
        await _autoTransferService.UpdateAutoTransferSettingsForStore(CurrentStore.Id, autoTransferSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Schedule record updated successfully.";
        return RedirectToAction(nameof(ListScheduleTransfer), new { storeId = CurrentStore.Id });
    }

    [HttpPost("save-schedule")]
    public async Task<IActionResult> SaveScheduledAutoPayment(string storeId, AutoTransferSettingsViewModel vm)
    {
        if (CurrentStore is null)
            return NotFound();

        if (vm.Destinations == null || !vm.Destinations.Any() || vm.Destinations.Any(d => HasNullProperties(d)))
        {
            TempData[WellKnownTempData.ErrorMessage] = "destination fields are required.";
            return RedirectToAction(nameof(ViewScheduledAutoTransfer), new { storeId = CurrentStore.Id });
        }
        var batchId = Encoders.Base58.EncodeData(RandomUtils.GetBytes(16));
        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        autoTransferSettings.ScheduledDestinations ??= new Dictionary<string, List<AutoTransferDestination>>();
        autoTransferSettings.ScheduledDestinations[batchId] = new List<AutoTransferDestination>(vm.Destinations);
        await _autoTransferService.UpdateAutoTransferSettingsForStore(CurrentStore.Id, autoTransferSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Schedule record saved successfully.";
        return RedirectToAction(nameof(ListScheduleTransfer), new { storeId = CurrentStore.Id });
    }

    [HttpGet("delete-schedule/{batchId}")]
    public async Task<IActionResult> DeletePrismDestination(string storeId, string batchId)
    {
        if (CurrentStore is null)
            return NotFound();


        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        if (!autoTransferSettings.ScheduledDestinations.ContainsKey(batchId))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Invalid batch destination specified";
            return RedirectToAction(nameof(ListScheduleTransfer), new { storeId = CurrentStore.Id });
        }
        return View("Confirm", new ConfirmModel("Delete configured schedule", $"Scheduled batch will be removed. Auto transfer will no longer work for all stores configured in this batch. Are you sure you want to proceed?", "Delete"));
    }

    [HttpPost("delete-schedule/{batchId}")]
    public async Task<IActionResult> DeletePrismDestinationPost(string storeId, string batchId)
    {
        if (CurrentStore is null)
            return NotFound();

        var autoTransferSettings = await _autoTransferService.GetAutoTransferSettings(CurrentStore.Id);
        if (autoTransferSettings.ScheduledDestinations.ContainsKey(batchId))
        {
            autoTransferSettings.ScheduledDestinations.Remove(batchId);
        }
        await _autoTransferService.UpdateAutoTransferSettingsForStore(CurrentStore.Id, autoTransferSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Schedule record removed successfully.";
        return RedirectToAction(nameof(ListScheduleTransfer), new { storeId = CurrentStore.Id });
    }

    private bool HasNullProperties(object obj)
    {
        if (obj == null) return true;

        var props = obj.GetType().GetProperties();
        return props.Any(p => p.GetValue(obj) == null);
    }
}
