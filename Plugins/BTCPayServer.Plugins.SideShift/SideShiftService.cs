using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.SideShift
{
    public class SideShiftService
    {
        private readonly ISettingsRepository _settingsRepository;
        private readonly IMemoryCache _memoryCache;
        private readonly IStoreRepository _storeRepository;

        public SideShiftService(ISettingsRepository settingsRepository, IMemoryCache memoryCache, IStoreRepository storeRepository)
        {
            _settingsRepository = settingsRepository;
            _memoryCache = memoryCache;
            _storeRepository = storeRepository;
        }
        
        public async Task<SideShiftSettings> GetSideShiftForStore(string storeId)
        {
            var k = $"{nameof(SideShiftSettings)}_{storeId}";
            return await _memoryCache.GetOrCreateAsync(k, async _ =>
            {
                var res = await _storeRepository.GetSettingAsync<SideShiftSettings>(storeId,
                    nameof(SideShiftSettings));
                if (res is not null) return res;
                res = await _settingsRepository.GetSettingAsync<SideShiftSettings>(k);

                if (res is not null)
                {
                    await SetSideShiftForStore(storeId, res);
                }

                await _settingsRepository.UpdateSetting<SideShiftSettings>(null, k);
                return res;
            });
        }

        public async Task SetSideShiftForStore(string storeId, SideShiftSettings SideShiftSettings)
        {
            var k = $"{nameof(SideShiftSettings)}_{storeId}";
            await _storeRepository.UpdateSetting(storeId, nameof(SideShiftSettings), SideShiftSettings);
            _memoryCache.Set(k, SideShiftSettings);
        }
    }
}
