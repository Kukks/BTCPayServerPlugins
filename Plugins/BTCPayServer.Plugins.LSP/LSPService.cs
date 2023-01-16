using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.LSP;

public class LSPService
{
    private readonly IMemoryCache _memoryCache;
    private readonly IStoreRepository _storeRepository;

    public LSPService(IMemoryCache memoryCache,
        IStoreRepository storeRepository)
    {
        _memoryCache = memoryCache;
        _storeRepository = storeRepository;
    }


    public async Task<LSPSettings> GetLSPForStore(string storeId)
    {
        var k = $"{nameof(LSPSettings)}_{storeId}";
        return await _memoryCache.GetOrCreateAsync(k, async _ =>
        {
            var res = await _storeRepository.GetSettingAsync<LSPSettings>(storeId,
                nameof(LSPSettings));
            return res;
        });
    }

    public async Task SetLSPForStore(string storeId, LSPSettings lspSettings)
    {
        var k = $"{nameof(LSPSettings)}_{storeId}";
        await _storeRepository.UpdateSetting(storeId, nameof(LSPSettings), lspSettings);
        _memoryCache.Set(k, lspSettings);
    }
}
