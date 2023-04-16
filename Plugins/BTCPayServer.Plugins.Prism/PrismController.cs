#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Filters;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.Prism;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("stores/{storeId}/plugins/prism")]
[ContentSecurityPolicy(CSPTemplate.AntiXSS, UnsafeInline = true)]
public class PrismController : Controller
{
    private readonly SatBreaker _satBreaker;

    public PrismController( SatBreaker satBreaker)
    {
        _satBreaker = satBreaker;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string storeId)
    {
        var settings =await _satBreaker.Get(storeId);
        return View(settings );
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string storeId, PrismSettings settings, string command)
    {
        for (int i = 0; i < settings.Splits?.Length; i++)
        {
            var prism = settings.Splits[i];
            if (string.IsNullOrEmpty(prism.Source))
            {
                ModelState.AddModelError($"Splits[{i}].Source", "Source is required");
            }
            else if(settings.Splits.Count(s => s.Source == prism.Source) > 1)
            {
                ModelState.AddModelError($"Splits[{i}].Source", "Sources must be unique");
            }
            if (!(prism.Destinations?.Length > 0))
            {

                ModelState.AddModelError($"Splits[{i}].Destinations", "At least one destination is required");
                continue;
            }

            var sum = prism.Destinations.Sum(d => d.Percentage);
            if (sum > 100)
            {

                ModelState.AddModelError($"Splits[{i}].Destinations", "Destinations must sum up to a 100 maximum");
            }

            for (int j = 0; j < prism.Destinations?.Length; j++)
            {
                var dest = prism.Destinations[j].Destination;
                //check that the source is a valid internet identifier, which is username@domain(and optional port)
                if (string.IsNullOrEmpty(dest))
                {
                    ModelState.AddModelError($"Splits[{i}].Destinations[{j}].Destination", "Destination is required");
                    continue;
                }

                try
                {
                    LNURL.LNURL.ExtractUriFromInternetIdentifier(dest);
                }
                catch (Exception e)
                {
                    try
                    {
                        LNURL.LNURL.Parse(dest, out var tag);
                    }
                    catch (Exception exception)
                    {
                        
                        ModelState.AddModelError($"Splits[{i}].Destinations[{j}].Destination", "Destination is not a valid LN address or LNURL");
                    }
                }
            }
            
        }
        

        if (!ModelState.IsValid)
        {
            return View(settings);
        }

        var settz = await _satBreaker.Get(storeId);
        settz.Splits = settings.Splits;
        settz.Enabled = settings.Enabled;
        settz.SatThreshold = settings.SatThreshold;
        var updateResult = await _satBreaker.UpdatePrismSettingsForStore(storeId, settz);
        if (!updateResult)
        {
            ModelState.AddModelError("VersionConflict", "The settings have been updated by another process. Please refresh the page and try again.");
            
            return View(settings);
        }
        TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Successfully saved settings"
        });
        return RedirectToAction("Edit", new {storeId});
    }

}