using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NNostr.Client;

namespace BTCPayServer.Plugins.NIP05;

public class LnurlDescriptionFilter : PluginHookFilter<string>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly Nip5Controller _nip5Controller;
    private readonly LightningAddressService _lightningAddressService;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<LnurlDescriptionFilter> _logger;

    public LnurlDescriptionFilter(IHttpContextAccessor httpContextAccessor,
        Nip5Controller nip5Controller, LightningAddressService lightningAddressService,
        InvoiceRepository invoiceRepository, IMemoryCache memoryCache, ILogger<LnurlDescriptionFilter> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _nip5Controller = nip5Controller;
        _lightningAddressService = lightningAddressService;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public override string Hook => "modify-lnurlp-description";

    public override async Task<string> Execute(string arg)
    {
        if(_httpContextAccessor.HttpContext is null)
            return arg;
        if (_httpContextAccessor.HttpContext.Request.Query.TryGetValue("nostr", out var nostr) &&
            (_httpContextAccessor.HttpContext.Request.RouteValues.TryGetValue("invoiceId", out var invoiceId) ||
             _httpContextAccessor.HttpContext.Items.TryGetValue("invoiceId", out invoiceId)
             ))
        {
            try
            {
                var metadata = JsonConvert.DeserializeObject<string[][]>(arg);
                var username = metadata
                    .FirstOrDefault(strings => strings.FirstOrDefault()?.Equals("text/identifier") is true)
                    ?.ElementAtOrDefault(1)?.ToLowerInvariant().Split("@")[0];
                if (!string.IsNullOrEmpty(username))
                {
                    var lnAddress = await _lightningAddressService.ResolveByAddress(username);
                    if (lnAddress is null)
                    {
                        return arg;
                    }
                }
                var parsedNote = System.Text.Json.JsonSerializer.Deserialize<NostrEvent>(nostr);
                if (parsedNote?.Kind != 9734)
                {
                    return arg;
                }

                if (!parsedNote.Verify())
                {
                    return arg;
                }

                using var entry = _memoryCache.CreateEntry(Nip05Plugin.GetZapRequestCacheKey(invoiceId.ToString()));
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                entry.SetValue(nostr);
                return nostr;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error while processing nostr zap request for invoice  {invoiceId}\n{nostr}");
            }
        }

        return arg;
    }
}