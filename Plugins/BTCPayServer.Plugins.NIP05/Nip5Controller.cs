#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using NNostr.Client;
using NNostr.Client.Protocols;

namespace BTCPayServer.Plugins.NIP05;

[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("stores/{storeId}/plugins/nip5")]
public class Nip5Controller : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly IMemoryCache _memoryCache;

    public Nip5Controller()
    {
    }

    public Nip5Controller(StoreRepository storeRepository,
        IMemoryCache memoryCache)
    {
        _storeRepository = storeRepository;
        _memoryCache = memoryCache;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string storeId)
    {
        var settings = await _storeRepository.GetSettingAsync<Nip5StoreSettings>(storeId, "NIP05");
        return View(settings ?? new());
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string storeId, Nip5StoreSettings settings, string command)
    {
        if (command == "remove")
        {
            var settingss = await _storeRepository.GetSettingAsync<Nip5StoreSettings>(storeId, "NIP05");
            if (settingss is not null)
            {
                await _storeRepository.UpdateSetting<Nip5StoreSettings>(storeId, "NIP05", null);

                _memoryCache.Remove($"NIP05_{settingss.Name.ToLowerInvariant()}");
            }

            return RedirectToAction("Edit", new {storeId});
        }

        try
        {
            settings.PubKey = settings.PubKey.Trim();
            settings.PubKey = settings.PubKey.FromNIP19Npub().ToHex();
        }
        catch (Exception)
        {
            try
            {
                
                var note = (NIP19.NosteProfileNote)settings.PubKey.FromNIP19Note() ;
                settings.PubKey = note.PubKey;
                settings.Relays = (settings.Relays ?? new string[0])?.Concat(note.Relays).ToArray();
            }
            catch (Exception)
            {
            }
        }

        try
        {
            NostrExtensions.ParsePubKey(settings.PubKey);
        }
        catch (Exception e)
        {
            
            ModelState.AddModelError(nameof(settings.PubKey), "invalid public key");
        }

        if (!ModelState.IsValid)
        {
            return View(settings);
        }

        settings.Relays = settings.Relays
            ?.Where(s => !string.IsNullOrEmpty(s) && Uri.TryCreate(s, UriKind.Absolute, out _)).Distinct().ToArray();
        var found = await Get(settings.Name.ToLowerInvariant());
        if (found.storeId is not null && storeId != found.storeId)
        {
            ModelState.AddModelError(nameof(settings.Name), "Name is already in use. Choose something else");

            return View(settings);
        }

        var settingssx = await _storeRepository.GetSettingAsync<Nip5StoreSettings>(storeId, "NIP05");
        if (settingssx is not null)
        {
            _memoryCache.Remove($"NIP05_{settingssx.Name.ToLowerInvariant()}");
        }

        await _storeRepository.UpdateSetting(storeId, "NIP05", settings);
        return RedirectToAction("Edit", new {storeId});
    }

    private async Task<(string? storeId, Nip5StoreSettings? settings)> Get(string name)
    {
        var rex = await _memoryCache.GetOrCreateAsync<(string? storeId, Nip5StoreSettings? settings)>(
            $"NIP05_{name.ToLowerInvariant()}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

                var store = await _storeRepository.GetSettingsAsync<Nip5StoreSettings>("NIP05");

                KeyValuePair<string, Nip5StoreSettings> matched = store.FirstOrDefault(pair =>
                    pair.Value.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));

                return (matched.Key, matched.Value);
            });
        if (rex.storeId is null)
        {
            _memoryCache.Remove($"NIP05_{name.ToLowerInvariant()}");
        }

        return rex;
    }

    [HttpGet("~/.well-known/nostr.json")]
    [EnableCors(CorsPolicies.All)]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    public async Task<IActionResult> GetUser([FromQuery] string name)
    {
        var result = await Get(name);

        return result.storeId is null
            ? NotFound()
            : Ok(new Nip5Response()
            {
                Names = new Dictionary<string, string>()
                {
                    {name, result.settings.PubKey}
                },
                Relays = result.settings.Relays?.Any() is true
                    ? new Dictionary<string, string[]>()
                    {
                        {result.settings.PubKey, result.settings.Relays}
                    }
                    : null
            });
    }
}