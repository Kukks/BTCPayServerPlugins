#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Filters;
using BTCPayServer.Services.Stores;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
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
        var settings = await GetForStore(storeId);
        return View(settings ?? new());
    }

    [NonAction]
    public async Task<Nip5StoreSettings?> GetForStore(string storeId)
    {
        return await _memoryCache.GetOrCreateAsync("NIP05_" + storeId,
            async entry => await _storeRepository.GetSettingAsync<Nip5StoreSettings>(storeId, "NIP05"));
    }

    [NonAction]
    public async Task UpdateStore(string storeId, Nip5StoreSettings? settings)
    {
        _memoryCache.Remove("NIP05_" + storeId);
        await _storeRepository.UpdateSetting(storeId, "NIP05", settings);
        _memoryCache.CreateEntry("NIP05_" + storeId).SetValue(settings);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string storeId, Nip5StoreSettings settings, string command)
    {
        var existingSettings = await GetForStore(storeId);
        if (command == "remove")
        {
            if (existingSettings is not null)
            {
                await UpdateStore(storeId, null);
                _memoryCache.Remove($"NIP05_{existingSettings.Name.ToLowerInvariant()}");
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
                if (!HexEncoder.IsWellFormed(settings.PubKey))
                {
                    var note = (NIP19.NosteProfileNote) settings.PubKey.FromNIP19Note();
                    settings.PubKey = note.PubKey;
                    settings.Relays = (settings.Relays ?? Array.Empty<string>())?.Concat(note.Relays).ToArray();
                }
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

        if (!string.IsNullOrEmpty(settings.PrivateKey))
        {
            try
            {
                ECPrivKey k;
                try
                {
                    k = settings.PrivateKey.FromNIP19Nsec();
                }
                catch (Exception e)
                {
                    k = NostrExtensions.ParseKey(settings.PrivateKey);
                }

                settings.PrivateKey = k.ToHex();
                if (string.IsNullOrEmpty(settings.PubKey))
                {
                    settings.PubKey = k.CreateXOnlyPubKey().ToHex();
                    ModelState.Remove(nameof(settings.PubKey));
                }
                else if (settings.PubKey != k.CreateXOnlyPubKey().ToHex())
                    ModelState.AddModelError(nameof(settings.PrivateKey),
                        "private key does not match public key provided. Clear the public key to generate it from the private key.");
            }
            catch (Exception e)
            {
                ModelState.AddModelError(nameof(settings.PrivateKey), "invalid private key");
            }
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

        if (existingSettings?.Name is not null)
        {
            _memoryCache.Remove($"NIP05_{existingSettings.Name.ToLowerInvariant()}");
        }

        await UpdateStore(storeId, settings);
        return RedirectToAction("Edit", new {storeId});
    }

    [NonAction]
    public async Task<(string? storeId, Nip5StoreSettings? settings)> Get(string name)
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

    [CheatModeRoute]
    [HttpGet("~/nostr-fake")]
    [EnableCors(CorsPolicies.All)]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    public async Task<IActionResult> FakeNostr(string lnurl)
    {
        if (lnurl.Contains("@"))
        {
            lnurl = LNURL.LNURL.ExtractUriFromInternetIdentifier(lnurl).ToString();
        }
        var lnurlRequest = (LNURLPayRequest) await LNURL.LNURL.FetchInformation(new Uri(lnurl), new HttpClient());
        var nKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
        var nostrEvent = new NostrEvent()
        {
            Kind = 9734,
            Content = "",

        };
        var lnurlBech32x = LNURL.LNURL.EncodeBech32(new Uri(lnurl));
        nostrEvent.SetTag("relays", "wss://btcpay.kukks.org/nostr");
        nostrEvent.SetTag("lnurl", lnurlBech32x);
        nostrEvent.SetTag("amount", lnurlRequest.MinSendable.MilliSatoshi.ToString());
        nostrEvent = await nostrEvent.ComputeIdAndSignAsync(nKey);
        var response = await new HttpClient().GetAsync(lnurlRequest.Callback + "?amount=" + lnurlRequest.MinSendable.MilliSatoshi +
                                  "&nostr=" +System.Text.Json.JsonSerializer.Serialize(nostrEvent));
        return Content(await response.Content.ReadAsStringAsync());
    }
}