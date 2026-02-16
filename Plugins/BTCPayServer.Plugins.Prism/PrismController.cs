#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Plugins.Prism.ViewModels;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Prism;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/{storeId}/prism")]
public class PrismController : Controller
{
    private readonly SatBreaker _satBreaker;
    private readonly LightningAddressService _lightningAddressService;
    private readonly PayoutProcessorService _payoutProcessorService;
    private readonly IEnumerable<IPayoutProcessorFactory> _payoutProcessorFactories;
    private readonly AppService _appService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPluginHookService _pluginHookService;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    private readonly EventAggregator _eventAggregator;

    public PrismController(
        SatBreaker satBreaker,
        LightningAddressService lightningAddressService,
        PayoutProcessorService payoutProcessorService,
        IEnumerable<IPayoutProcessorFactory> payoutProcessorFactories,
        AppService appService,
        UserManager<ApplicationUser> userManager,
        IPluginHookService pluginHookService,
        PullPaymentHostedService pullPaymentHostedService,
        EventAggregator eventAggregator)
    {
        _satBreaker = satBreaker;
        _lightningAddressService = lightningAddressService;
        _payoutProcessorService = payoutProcessorService;
        _payoutProcessorFactories = payoutProcessorFactories;
        _appService = appService;
        _userManager = userManager;
        _pluginHookService = pluginHookService;
        _pullPaymentHostedService = pullPaymentHostedService;
        _eventAggregator = eventAggregator;
    }

    [HttpGet("")]
    public async Task<IActionResult> Edit(string storeId)
    {
        var settings = await _satBreaker.Get(storeId) ?? new PrismSettings();

        var vm = new PrismViewModel
        {
            Enabled = settings.Enabled,
            SatThreshold = settings.SatThreshold,
            Reserve = settings.Reserve,
            Version = settings.Version,
            StoreId = storeId,
            Destinations = settings.Destinations ?? new Dictionary<string, PrismDestination>(),
            DestinationBalances = settings.DestinationBalance ?? new Dictionary<string, long>(),
            PendingPayouts = settings.PendingPayouts ?? new Dictionary<string, PendingPayout>(),
            Splits = settings.Splits?.Select(MapSplitToViewModel).ToList() ?? new List<SplitViewModel>()
        };

        await PopulateDisplayData(vm, storeId);
        return View("Prism/Edit", vm);
    }

    [HttpPost("")]
    public async Task<IActionResult> Edit(string storeId, PrismViewModel vm, string command)
    {
        if (command == "add-split")
        {
            ModelState.Clear();
            vm.Splits.Add(new SplitViewModel());
            await PopulateDisplayData(vm, storeId);
            return View("Prism/Edit", vm);
        }

        if (command == "add-wallettransfer")
        {
            ModelState.Clear();
            vm.Splits.Add(new SplitViewModel
            {
                SourceType = "wallettransfer",
                TransferPaymentMethod = "BTC-CHAIN",
                TransferFrequency = "M",
                TransferDay = "1"
            });
            await PopulateDisplayData(vm, storeId);
            return View("Prism/Edit", vm);
        }

        if (command.StartsWith("remove-split:"))
        {
            var index = int.Parse(command["remove-split:".Length..]);
            ModelState.Clear();
            vm.Splits.RemoveAt(index);
            await PopulateDisplayData(vm, storeId);
            return View("Prism/Edit", vm);
        }

        if (command.StartsWith("add-destination:"))
        {
            var splitIndex = int.Parse(command["add-destination:".Length..]);
            ModelState.Clear();
            vm.Splits[splitIndex].Destinations.Add(new SplitDestinationViewModel());
            await PopulateDisplayData(vm, storeId);
            return View("Prism/Edit", vm);
        }

        if (command.StartsWith("remove-destination:"))
        {
            var parts = command["remove-destination:".Length..].Split(':');
            var splitIndex = int.Parse(parts[0]);
            var destIndex = int.Parse(parts[1]);
            ModelState.Clear();
            vm.Splits[splitIndex].Destinations.RemoveAt(destIndex);
            await PopulateDisplayData(vm, storeId);
            return View("Prism/Edit", vm);
        }

        // "save" command
        var settings = await _satBreaker.Get(storeId) ?? new PrismSettings();

        // Rebuild each split's Source from the type-specific fields
        for (var i = 0; i < vm.Splits.Count; i++)
        {
            var split = vm.Splits[i];
            split.Source = RebuildSource(split);
        }

        // Validate splits
        for (var i = 0; i < vm.Splits.Count; i++)
        {
            var split = vm.Splits[i];

            if (string.IsNullOrEmpty(split.Source))
            {
                ModelState.AddModelError($"Splits[{i}].Source", "Source is required");
            }
            else if (split.SourceType != "wallettransfer" &&
                     vm.Splits.Where((s, idx) => idx != i && s.SourceType != "wallettransfer")
                         .Any(s => s.Source == split.Source))
            {
                ModelState.AddModelError($"Splits[{i}].Source", "Sources must be unique");
            }

            if (split.Destinations.Count == 0)
            {
                ModelState.AddModelError($"Splits[{i}].Destinations", "At least one destination is required");
                continue;
            }

            var sum = split.Destinations.Sum(d => d.Percentage);
            if (sum > 100)
            {
                ModelState.AddModelError($"Splits[{i}].Destinations", "Destination percentages must sum to 100 or less");
            }

            // Determine expected payout method for wallet transfers
            PayoutMethodId? expectedPayoutMethod = null;
            if (split.SourceType == "wallettransfer" && !string.IsNullOrEmpty(split.Source))
            {
                var parsed = SatBreaker.ParseSource(split.Source);
                if (parsed?.paymentMethod != null)
                {
                    var pmStr = parsed.Value.paymentMethod.ToString();
                    expectedPayoutMethod = pmStr.Contains("LN")
                        ? PayoutTypes.LN.GetPayoutMethodId("BTC")
                        : PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
                }
            }

            for (var j = 0; j < split.Destinations.Count; j++)
            {
                var dest = split.Destinations[j];
                if (string.IsNullOrEmpty(dest.Destination))
                {
                    ModelState.AddModelError($"Splits[{i}].Destinations[{j}].Destination", "Destination is required");
                    continue;
                }

                // Resolve alias to raw destination for validation
                var rawDest = dest.Destination;
                if (settings.Destinations != null && settings.Destinations.TryGetValue(rawDest, out var aliased))
                {
                    rawDest = aliased.Destination;
                }

                var result = await _pluginHookService.ApplyFilter("prism-destination-validate", rawDest);
                if (result is not PrismDestinationValidationResult validationResult || !validationResult.Success)
                {
                    ModelState.AddModelError($"Splits[{i}].Destinations[{j}].Destination", "Destination is not valid");
                    continue;
                }

                if (expectedPayoutMethod != null && validationResult.PayoutMethodId != null &&
                    validationResult.PayoutMethodId != expectedPayoutMethod)
                {
                    var expectedType = expectedPayoutMethod.ToString().Contains("LN") ? "Lightning" : "On-chain";
                    var actualType = validationResult.PayoutMethodId.ToString().Contains("LN") ? "Lightning" : "On-chain";
                    ModelState.AddModelError($"Splits[{i}].Destinations[{j}].Destination",
                        $"Destination is {actualType} but transfer is {expectedType}");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            await PopulateDisplayData(vm, storeId);
            return View("Prism/Edit", vm);
        }

        // Map back to PrismSettings
        settings.Enabled = vm.Enabled;
        settings.SatThreshold = vm.SatThreshold;
        settings.Reserve = vm.Reserve;
        settings.Splits = vm.Splits.Select(s => new Split
        {
            Source = s.Source!,
            Destinations = s.Destinations
                .Where(d => !string.IsNullOrEmpty(d.Destination))
                .Select(d => new PrismSplit
                {
                    Destination = d.Destination!,
                    Percentage = d.Percentage
                })
                .ToList()
        }).ToList();

        var success = await _satBreaker.UpdatePrismSettingsForStore(storeId, settings);
        if (!success)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Settings updated by another process, please refresh";
        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = "Settings saved";
        }

        return RedirectToAction(nameof(Edit), new { storeId });
    }

    [HttpGet("destination")]
    public async Task<IActionResult> EditDestination(string storeId, string? id)
    {
        var settings = await _satBreaker.Get(storeId) ?? new PrismSettings();

        var vm = new EditDestinationViewModel { StoreId = storeId };

        if (!string.IsNullOrEmpty(id) && settings.Destinations != null &&
            settings.Destinations.TryGetValue(id, out var prismDest))
        {
            vm.Id = id;
            vm.OriginalId = id;
            vm.SatThreshold = prismDest.SatThreshold;
            vm.Reserve = prismDest.Reserve;
            vm.IsInUse = settings.Splits?.Any(s =>
                s.Destinations.Any(d => d.Destination == id)) ?? false;

            if (prismDest.Destination.StartsWith("store-prism:", StringComparison.OrdinalIgnoreCase))
            {
                vm.DestinationType = "store";
                var parts = prismDest.Destination.Split(':');
                if (parts.Length >= 2)
                    vm.SelectedStoreId = parts[1];
                if (parts.Length >= 3)
                    vm.StorePaymentMethod = parts[2];
            }
            else
            {
                vm.DestinationType = "address";
                vm.AddressValue = prismDest.Destination;
            }
        }

        await PopulateAvailableStores(vm, storeId);
        return View("Prism/EditDestination", vm);
    }

    [HttpPost("destination")]
    public async Task<IActionResult> EditDestination(string storeId, EditDestinationViewModel vm, string command)
    {
        if (command == "delete")
        {
            var settings = await _satBreaker.Get(storeId);
            if (settings?.Destinations != null && !string.IsNullOrEmpty(vm.OriginalId))
            {
                var isInUse = settings.Splits?.Any(s =>
                    s.Destinations.Any(d => d.Destination == vm.OriginalId)) ?? false;
                if (isInUse)
                {
                    TempData[WellKnownTempData.ErrorMessage] = "Cannot delete a destination alias that is in use by a split";
                    return RedirectToAction(nameof(EditDestination), new { storeId, id = vm.OriginalId });
                }

                settings.Destinations.Remove(vm.OriginalId);
                await _satBreaker.UpdatePrismSettingsForStore(storeId, settings);
                TempData[WellKnownTempData.SuccessMessage] = "Destination deleted";
            }

            return RedirectToAction(nameof(Edit), new { storeId });
        }

        // "save" command
        if (string.IsNullOrEmpty(vm.Id))
        {
            ModelState.AddModelError(nameof(vm.Id), "Alias name is required");
        }

        // Build the destination string
        string destination;
        if (vm.DestinationType == "store")
        {
            destination = $"store-prism:{vm.SelectedStoreId}:{vm.StorePaymentMethod}";
        }
        else
        {
            destination = vm.AddressValue ?? string.Empty;
        }

        if (string.IsNullOrEmpty(destination))
        {
            ModelState.AddModelError(nameof(vm.AddressValue), "Destination address is required");
        }
        else
        {
            var result = await _pluginHookService.ApplyFilter("prism-destination-validate", destination);
            if (result is not PrismDestinationValidationResult validationResult || !validationResult.Success)
            {
                ModelState.AddModelError(nameof(vm.AddressValue), "Destination is not valid");
            }
        }

        var currentSettings = await _satBreaker.Get(storeId) ?? new PrismSettings();
        currentSettings.Destinations ??= new Dictionary<string, PrismDestination>();

        // Check alias name uniqueness (if new or renamed)
        if (!string.IsNullOrEmpty(vm.Id) && vm.Id != vm.OriginalId &&
            currentSettings.Destinations.ContainsKey(vm.Id))
        {
            ModelState.AddModelError(nameof(vm.Id), "An alias with this name already exists");
        }

        if (!ModelState.IsValid)
        {
            vm.StoreId = storeId;
            await PopulateAvailableStores(vm, storeId);
            return View("Prism/EditDestination", vm);
        }

        var newDest = new PrismDestination
        {
            Destination = destination,
            SatThreshold = vm.SatThreshold,
            Reserve = vm.Reserve
        };

        // Handle rename: update all split references and balance keys
        if (!string.IsNullOrEmpty(vm.OriginalId) && vm.OriginalId != vm.Id)
        {
            currentSettings.Destinations.Remove(vm.OriginalId);

            if (currentSettings.Splits != null)
            {
                foreach (var split in currentSettings.Splits)
                {
                    foreach (var splitDest in split.Destinations.Where(d => d.Destination == vm.OriginalId))
                    {
                        splitDest.Destination = vm.Id!;
                    }
                }
            }

            if (currentSettings.DestinationBalance.Remove(vm.OriginalId, out var balance))
            {
                currentSettings.DestinationBalance[vm.Id!] = balance;
            }
        }

        currentSettings.Destinations[vm.Id!] = newDest;

        await _satBreaker.UpdatePrismSettingsForStore(storeId, currentSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Destination saved";
        return RedirectToAction(nameof(Edit), new { storeId });
    }

    [HttpPost("update-balance")]
    public async Task<IActionResult> UpdateBalance(string storeId, string destinationId, long newBalance)
    {
        await _satBreaker.WaitAndLock(storeId);
        try
        {
            var settings = _satBreaker.GetInternal(storeId);
            settings.DestinationBalance ??= new Dictionary<string, long>();

            if (newBalance == 0)
            {
                settings.DestinationBalance.Remove(destinationId);
            }
            else
            {
                settings.DestinationBalance[destinationId] = newBalance * 1000;
            }

            await _satBreaker.UpdatePrismSettingsForStore(storeId, settings, skipLock: true);
            _satBreaker.TriggerPayoutCheck();
        }
        finally
        {
            _satBreaker.Unlock(storeId);
        }

        TempData[WellKnownTempData.SuccessMessage] = "Balance updated";
        return RedirectToAction(nameof(Edit), new { storeId });
    }

    [HttpPost("cancel-payout")]
    public async Task<IActionResult> CancelPayout(string storeId, string payoutId)
    {
        var result = await _pullPaymentHostedService.Cancel(
            new PullPaymentHostedService.CancelRequest(new[] { payoutId }, new[] { storeId }));

        var payoutResult = result.FirstOrDefault().Value;
        if (payoutResult == MarkPayoutRequest.PayoutPaidResult.Ok)
        {
            _satBreaker.TriggerPayoutCheck();
            TempData[WellKnownTempData.SuccessMessage] =
                "Payout cancelled (if the threshold is still within reach, a new payout will be created in its place)";
        }
        else
        {
            TempData[WellKnownTempData.ErrorMessage] = payoutResult switch
            {
                MarkPayoutRequest.PayoutPaidResult.NotFound => "Payout not found",
                MarkPayoutRequest.PayoutPaidResult.InvalidState => "Payout was in a non-cancellable state",
                _ => "Unknown error"
            };
        }

        return RedirectToAction(nameof(Edit), new { storeId });
    }

    [HttpPost("send-now")]
    public IActionResult SendNow(string storeId, string splitSource)
    {
        _eventAggregator.Publish(new SatBreaker.ScheduleDayEvent(splitSource));
        TempData[WellKnownTempData.SuccessMessage] = "Transfer triggered";
        return RedirectToAction(nameof(Edit), new { storeId });
    }

    private async Task PopulateDisplayData(PrismViewModel vm, string storeId)
    {
        // Load available stores
        var user = await _userManager.GetUserAsync(User);
        if (user != null)
        {
            var appUser = await _userManager.Users
                .Where(c => c.Id == user.Id)
                .Include(u => u.UserStores)
                .ThenInclude(us => us.StoreData)
                .SingleOrDefaultAsync();

            vm.AvailableStores = appUser?.UserStores
                .Where(s => s.StoreDataId != storeId && !s.StoreData.Archived)
                .Select(s => new SelectListItem { Value = s.StoreData.Id, Text = s.StoreData.StoreName })
                .ToList() ?? new List<SelectListItem>();
        }

        // Load LN addresses
        var lnAddresses = await _lightningAddressService.Get(
            new LightningAddressQuery { StoreIds = new[] { storeId } });
        vm.AvailableLnAddresses = lnAddresses
            .Select(a => new SelectListItem { Value = a.Username, Text = a.Username })
            .ToList();

        // Load POS apps and products
        var posApps = (await _appService.GetApps(PointOfSaleAppType.AppType))
            .Where(c => c.StoreDataId == storeId && !c.Archived)
            .ToList();
        vm.AvailableApps = posApps
            .Select(a => new SelectListItem { Value = a.Id, Text = a.Name })
            .ToList();
        vm.AppProducts = new Dictionary<string, List<SelectListItem>>();
        foreach (var app in posApps)
        {
            var appSettings = app.GetSettings<PointOfSaleSettings>();
            if (!string.IsNullOrEmpty(appSettings?.Template))
            {
                var items = AppService.Parse(appSettings.Template);
                vm.AppProducts[app.Id] = items?
                    .Select(item => new SelectListItem { Value = item.Id, Text = item.Title })
                    .ToList() ?? new List<SelectListItem>();
            }
        }

        // Load payout processors
        var pmi = PayoutTypes.LN.GetPayoutMethodId("BTC");
        var pmichain = PayoutTypes.CHAIN.GetPayoutMethodId("BTC");
        var processors = await _payoutProcessorService.GetProcessors(
            new PayoutProcessorService.PayoutProcessorQuery
            {
                Stores = new[] { storeId },
                PayoutMethods = new[] { pmi, pmichain }
            });
        vm.HasLnProcessor = processors.Any(p => p.GetPayoutMethodId() == pmi);
        vm.HasChainProcessor = processors.Any(p => p.GetPayoutMethodId() == pmichain);

        // Reload destinations, balances, and pending payouts from current settings
        var currentSettings = await _satBreaker.Get(storeId);
        vm.Destinations = currentSettings?.Destinations ?? new Dictionary<string, PrismDestination>();
        vm.DestinationBalances = currentSettings?.DestinationBalance ?? new Dictionary<string, long>();
        vm.PendingPayouts = currentSettings?.PendingPayouts ?? new Dictionary<string, PendingPayout>();
    }

    private async Task PopulateAvailableStores(EditDestinationViewModel vm, string storeId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user != null)
        {
            var appUser = await _userManager.Users
                .Where(c => c.Id == user.Id)
                .Include(u => u.UserStores)
                .ThenInclude(us => us.StoreData)
                .SingleOrDefaultAsync();

            vm.AvailableStores = appUser?.UserStores
                .Where(s => s.StoreDataId != storeId && !s.StoreData.Archived)
                .Select(s => new SelectListItem { Value = s.StoreData.Id, Text = s.StoreData.StoreName })
                .ToList() ?? new List<SelectListItem>();
        }
    }

    private static SplitViewModel MapSplitToViewModel(Split split)
    {
        var vm = new SplitViewModel
        {
            Source = split.Source,
            Destinations = split.Destinations
                .Select(d => new SplitDestinationViewModel
                {
                    Destination = d.Destination,
                    Percentage = d.Percentage
                })
                .ToList()
        };

        if (string.IsNullOrEmpty(split.Source))
        {
            vm.SourceType = "catchall";
            return vm;
        }

        if (split.Source.StartsWith("storetransfer:", StringComparison.OrdinalIgnoreCase))
        {
            vm.SourceType = "wallettransfer";
            var parsed = SatBreaker.ParseSource(split.Source);
            if (parsed != null)
            {
                vm.TransferPaymentMethod = parsed.Value.paymentMethod.ToString();
                vm.TransferAmount = parsed.Value.amount?.ToUnit(LightMoneyUnit.Satoshi).ToString();

                switch (parsed.Value.schedule.Frequency)
                {
                    case SatBreaker.TransferFrequency.Daily:
                        vm.TransferFrequency = "D";
                        break;
                    case SatBreaker.TransferFrequency.Weekly:
                        vm.TransferFrequency = "W";
                        break;
                    case SatBreaker.TransferFrequency.Monthly:
                        vm.TransferFrequency = "M";
                        break;
                }

                vm.TransferDay = parsed.Value.schedule.DayValue.ToString();
            }

            return vm;
        }

        if (split.Source.StartsWith("pos:", StringComparison.OrdinalIgnoreCase))
        {
            vm.SourceType = "pos";
            var parts = split.Source.Split(':');
            if (parts.Length >= 3)
            {
                vm.PosAppId = parts[1];
                vm.PosProductId = parts[2];
                vm.PosPaymentFilter = parts.Length >= 4 ? parts[3] : "";
            }

            return vm;
        }

        if (split.Source.StartsWith("*"))
        {
            vm.SourceType = "catchall";
            vm.CatchAllType = split.Source;
            return vm;
        }

        // Default: lightning address
        vm.SourceType = "lnaddress";
        vm.LnAddress = split.Source;
        return vm;
    }

    private static string? RebuildSource(SplitViewModel split)
    {
        switch (split.SourceType)
        {
            case "catchall":
                return string.IsNullOrEmpty(split.CatchAllType) ? "*All" : split.CatchAllType;

            case "lnaddress":
                return split.LnAddress;

            case "pos":
            {
                if (string.IsNullOrEmpty(split.PosAppId) || string.IsNullOrEmpty(split.PosProductId))
                    return null;
                var source = $"pos:{split.PosAppId}:{split.PosProductId}";
                if (!string.IsNullOrEmpty(split.PosPaymentFilter))
                    source += $":{split.PosPaymentFilter}";
                return source;
            }

            case "wallettransfer":
            {
                SatBreaker.TransferFrequency frequency;
                switch (split.TransferFrequency)
                {
                    case "D":
                        frequency = SatBreaker.TransferFrequency.Daily;
                        break;
                    case "W":
                        frequency = SatBreaker.TransferFrequency.Weekly;
                        break;
                    case "M":
                        frequency = SatBreaker.TransferFrequency.Monthly;
                        break;
                    default:
                        frequency = SatBreaker.TransferFrequency.Monthly;
                        break;
                }

                var dayValue = 1;
                if (!string.IsNullOrEmpty(split.TransferDay) && int.TryParse(split.TransferDay, out var parsed))
                    dayValue = parsed;

                var schedule = new SatBreaker.TransferSchedule(frequency, dayValue);

                var paymentMethod = string.IsNullOrEmpty(split.TransferPaymentMethod)
                    ? PaymentTypes.LN.GetPaymentMethodId("BTC")
                    : PaymentMethodId.Parse(split.TransferPaymentMethod);

                LightMoney? amount = null;
                if (!string.IsNullOrEmpty(split.TransferAmount) && long.TryParse(split.TransferAmount, out var sats))
                    amount = LightMoney.Satoshis(sats);

                return SatBreaker.EncodeSource(schedule, paymentMethod, amount);
            }

            default:
                return null;
        }
    }
}
