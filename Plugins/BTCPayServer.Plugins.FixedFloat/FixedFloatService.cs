using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.FixedFloat
{
    public class FixedFloatService
    {
        private readonly ISettingsRepository _settingsRepository;
        private readonly IStoreRepository _storeRepository;
        private readonly IMemoryCache _memoryCache;

        public FixedFloatService(ISettingsRepository settingsRepository, IStoreRepository storeRepository, IMemoryCache memoryCache)
        {
            _settingsRepository = settingsRepository;
            _storeRepository = storeRepository;
            _memoryCache = memoryCache;
        }
        public async Task<FixedFloatSettings> GetFixedFloatForStore(string storeId)
        {
            var k = $"{nameof(FixedFloatSettings)}_{storeId}";
            return await _memoryCache.GetOrCreateAsync(k, async _ =>
            {
                var res = await _storeRepository.GetSettingAsync<FixedFloatSettings>(storeId,
                    nameof(FixedFloatSettings));
                if (res is not null) return res;
                res = await _settingsRepository.GetSettingAsync<FixedFloatSettings>(k);

                if (res is not null)
                {
                    await SetFixedFloatForStore(storeId, res);
                }

                await _settingsRepository.UpdateSetting<FixedFloatSettings>(null, k);
                return res;
            });
        }

        public async Task SetFixedFloatForStore(string storeId, FixedFloatSettings fixedFloatSettings)
        {
            var k = $"{nameof(FixedFloatSettings)}_{storeId}";
            await _storeRepository.UpdateSetting(storeId, nameof(FixedFloatSettings), fixedFloatSettings);
            _memoryCache.Remove(k);
        }
    }
}
