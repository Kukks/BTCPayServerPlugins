using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Hosting;
using NicolasDorier.RateLimits;

namespace BTCPayServer.Plugins.DynamicRateLimits;

public class DynamicRateLimitsService : IHostedService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly RateLimitService _rateLimitService;
    public IEnumerable<string> OriginalLimits { get; private set;  }

    public DynamicRateLimitsService(ISettingsRepository  settingsRepository, RateLimitService rateLimitService)
    {
        _settingsRepository = settingsRepository;
        _rateLimitService = rateLimitService;
    }
        
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        OriginalLimits =
            ((ConcurrentDictionary<string, LimitRequestZone>) _rateLimitService.GetType()
                .GetField("_Zones", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_rateLimitService))
            .Values.Select(zone => zone.ToString());
            
        var settings = await _settingsRepository.GetSettingAsync<DynamicRateLimitSettings>();
        if (settings?.RateLimits is not null)
        {
            foreach (var limit in settings.RateLimits)
            {
                _rateLimitService.SetZone(limit);
            }
        }
    }

    public async Task<DynamicRateLimitSettings> Get()
    {
        
        return  (await _settingsRepository.GetSettingAsync<DynamicRateLimitSettings>())?? new DynamicRateLimitSettings();
    }

    public async Task UseDefaults()
    {
        foreach (var originalLimit in OriginalLimits)
        {
            _rateLimitService.SetZone(originalLimit);
        }

        await _settingsRepository.UpdateSetting(new DynamicRateLimitSettings());
    }

    public async Task Update(string[] limits)
    {
        foreach (var originalLimit in OriginalLimits)
        {
            _rateLimitService.SetZone(originalLimit);
        }
        foreach (var limit in limits)
        {
            _rateLimitService.SetZone(limit);
        }
            
        await _settingsRepository.UpdateSetting(new DynamicRateLimitSettings()
        {
            RateLimits = limits
        });
    }
        
        

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}