using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.AOPP
{
    public class AOPPService
    {
        private readonly ISettingsRepository _settingsRepository;
        private readonly IMemoryCache _memoryCache;
        private readonly IStoreRepository _storeRepository;

        public AOPPService(ISettingsRepository settingsRepository,  IMemoryCache memoryCache,
            IStoreRepository storeRepository)
        {
            _settingsRepository = settingsRepository;
            _memoryCache = memoryCache;
            _storeRepository = storeRepository;
        }


        public async Task<AOPPSettings> GetAOPPForStore(string storeId)
        {
            var k = $"{nameof(AOPPSettings)}_{storeId}";
            return await _memoryCache.GetOrCreateAsync(k, async _ =>
            {
                var res = await _storeRepository.GetSettingAsync<AOPPSettings>(storeId,
                    nameof(AOPPSettings));
                if (res is not null) return res;
                res = await _settingsRepository.GetSettingAsync<AOPPSettings>(k);

                if (res is not null)
                {
                    await SetAOPPForStore(storeId, res);
                }

                await _settingsRepository.UpdateSetting<AOPPSettings>(null, k);
                return res;
            });
        }

        public async Task SetAOPPForStore(string storeId, AOPPSettings AOPPSettings)
        {
            var k = $"{nameof(AOPPSettings)}_{storeId}";
            await _storeRepository.UpdateSetting(storeId, nameof(AOPPSettings), AOPPSettings);
            _memoryCache.Set(k, AOPPSettings);
        }


    }
}
