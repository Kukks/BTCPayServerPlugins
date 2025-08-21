#nullable enable
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Plugins.Prism.ViewModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Prism;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("stores/{storeId}/plugins/prism/")]
[ContentSecurityPolicy(CSPTemplate.AntiXSS, UnsafeInline = true)]
public class PrismController : Controller
{
    private readonly SatBreaker _satBreaker;
    public PrismController(SatBreaker satBreaker)
    {
        _satBreaker = satBreaker;
    }
    public StoreData CurrentStore => HttpContext.GetStoreData();

    [HttpGet]
    public async Task<IActionResult> Edit()
    {
        return View();
    }

    [HttpGet("manage")]
    public async Task<IActionResult> ManagePrism()
    {
        if (CurrentStore is null)
            return NotFound();

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        return View(prismSettings ?? new PrismSettings());
    }

    [HttpGet("settings")]
    public async Task<IActionResult> PrismSetting()
    {
        if (CurrentStore is null)
            return NotFound();

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        return View(prismSettings ?? new PrismSettings());
    }


    [HttpPost("settings")]
    public async Task<IActionResult> PrismSettingPost(PrismSettings vm)
    {
        if (CurrentStore is null)
            return NotFound();

        var groupedSources = vm.Splits.GroupBy(s => s.Source)
        .Select(g => new
        {
            Source = g.Key,
            Count = g.Count(),
            TotalPercentage = g.SelectMany(s => s.Destinations).Sum(d => d.Percentage)
        }).ToList();

        var duplicateSources = groupedSources.Where(x => x.Count > 1).Select(x => x.Source).ToList();
        var invalidSources = groupedSources.Where(x => x.TotalPercentage > 100).Select(x => x.Source).ToList();

        if (duplicateSources.Any())
        {
            TempData[WellKnownTempData.ErrorMessage] = "You cannot configure multiple of the same source. A source was configured more than once";
            return RedirectToAction(nameof(PrismSetting), new { storeId = CurrentStore.Id });
        }
        if (invalidSources.Any())
        {
            TempData[WellKnownTempData.ErrorMessage] = "The total percentage for a sources should not exceed 100";
            return RedirectToAction(nameof(PrismSetting), new { storeId = CurrentStore.Id });
        }

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        prismSettings.Enabled = vm.Enabled;
        prismSettings.SatThreshold = vm.SatThreshold;
        prismSettings.Reserve = vm.Reserve;
        prismSettings.Splits = vm.Splits;
        await _satBreaker.UpdatePrismSettingsForStore(CurrentStore.Id, prismSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Prism global settings updated successfully";
        return RedirectToAction(nameof(PrismSetting), new { storeId = CurrentStore.Id });
    }


    [HttpGet("destination")]
    public async Task<IActionResult> ListPrismDestination(string storeId)
    {
        if (CurrentStore is null)
            return NotFound();

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        return View(prismSettings ?? new PrismSettings());
    }


    [HttpGet("view")]
    public async Task<IActionResult> ViewPrismDestination(string storeId, string destinationId)
    {
        if (CurrentStore is null)
            return NotFound();

        DestinationViewModel vm = new();
        if (!string.IsNullOrEmpty(destinationId))
        {
            var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
            prismSettings.Destinations.TryGetValue(destinationId, out var destination);
            if (destination != null)
            {
                vm.StoreId = CurrentStore.Id;
                vm.DestinationId = destinationId;
                vm.Reserve = destination.Reserve;
                vm.Destination = destination.Destination;
                vm.SatThreshold = destination.SatThreshold;
                vm.PayoutMethodId = destination.PayoutMethodId;
            }
        }
        return View(vm);
    }


    [HttpPost("create-destination")]
    public async Task<IActionResult> CreatePrismDestination(string storeId, DestinationViewModel vm)
    {
        if (string.IsNullOrEmpty(CurrentStore.Id))
            return NotFound();

        if (string.IsNullOrEmpty(vm.DestinationId))
        {
            TempData[WellKnownTempData.ErrorMessage] = "Kindly input a destination address";
            return RedirectToAction(nameof(ViewPrismDestination), new { storeId = CurrentStore.Id });
        }
        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);

        prismSettings.Destinations.Add(vm.DestinationId, new PrismDestination { 
            Destination = vm.Destination, 
            Reserve = vm.Reserve,
            SatThreshold = vm.SatThreshold,
            PayoutMethodId = vm.PayoutMethodId
        });
        await _satBreaker.UpdatePrismSettingsForStore(CurrentStore.Id, prismSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Prism destination added successfully";
        return RedirectToAction(nameof(ListPrismDestination), new { storeId = CurrentStore.Id });
    }


    [HttpPost("update-destination/{destinationId}")]
    public async Task<IActionResult> UpdatePrismDestination(string storeId, string destinationId, DestinationViewModel vm)
    {
        if (string.IsNullOrEmpty(CurrentStore.Id))
            return NotFound();

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        prismSettings.Destinations.TryGetValue(destinationId, out var destination);
        if (destination == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Invalid destination specified";
            return RedirectToAction(nameof(ListPrismDestination), new { storeId = CurrentStore.Id });
        }

        prismSettings.Destinations[destinationId] = new PrismDestination
        {
            Destination = vm.Destination,
            Reserve = vm.Reserve,
            SatThreshold = vm.SatThreshold,
            PayoutMethodId = destination.PayoutMethodId
        };
        await _satBreaker.UpdatePrismSettingsForStore(CurrentStore.Id, prismSettings);
        TempData[WellKnownTempData.SuccessMessage] = "Prism destination updated successfully";
        return RedirectToAction(nameof(ListPrismDestination), new { storeId = CurrentStore.Id });
    }


    [HttpGet("delete/{destinationId}")]
    public async Task<IActionResult> DeletePrismDestination(string storeId, string destinationId)
    {
        if (CurrentStore is null)
            return NotFound();

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        prismSettings.Destinations.TryGetValue(destinationId, out var destination);
        if (destination == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Invalid destination specified";
            return RedirectToAction(nameof(ListPrismDestination), new { storeId = CurrentStore.Id });
        }
        return View("Confirm", new ConfirmModel("Delete destination details", $"Destination will be removed from the prism configuration. Are you sure you want to proceed?", "Delete"));
    }


    [HttpPost("delete/{destinationId}")]
    public async Task<IActionResult> DeletePrismDestinationPost(string storeId, string destinationId)
    {
        if (CurrentStore is null)
            return NotFound();

        var prismSettings = await _satBreaker.GetPrismSettings(CurrentStore.Id);
        prismSettings.Destinations.TryGetValue(destinationId, out var destination);
        if (destination == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Invalid destination specified";
            return RedirectToAction(nameof(ListPrismDestination), new { storeId = CurrentStore.Id });
        }
        prismSettings.Destinations.Remove(destinationId); 
        await _satBreaker.UpdatePrismSettingsForStore(CurrentStore.Id, prismSettings);

        TempData[WellKnownTempData.SuccessMessage] = "Destination removed successfully";
        return RedirectToAction(nameof(ListPrismDestination), new { storeId = CurrentStore.Id });
    }
}