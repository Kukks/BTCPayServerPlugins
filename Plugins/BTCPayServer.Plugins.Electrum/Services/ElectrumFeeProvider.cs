using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BTCPayServer.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Fee estimation via Electrum's blockchain.estimatefee.
/// Caches results for 30 seconds.
/// </summary>
public class ElectrumFeeProvider : IFeeProvider
{
    private readonly ElectrumClient _client;
    private readonly ConcurrentDictionary<int, (FeeRate Rate, DateTimeOffset CachedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public ElectrumFeeProvider(ElectrumClient client)
    {
        _client = client;
    }

    public async Task<FeeRate> GetFeeRateAsync(int blockTarget = 20)
    {
        if (_cache.TryGetValue(blockTarget, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
        {
            return cached.Rate;
        }

        try
        {
            var btcPerKb = await _client.EstimateFeeAsync(blockTarget, default);

            FeeRate rate;
            if (btcPerKb <= 0)
            {
                // Server can't estimate, use minimum
                rate = new FeeRate(1.0m);
            }
            else
            {
                // Convert BTC/kB to sat/vB
                // 1 BTC = 100,000,000 sat, 1 kB = 1000 bytes
                // sat/vB = (BTC/kB * 100,000,000) / 1000 = BTC/kB * 100,000
                var satPerByte = btcPerKb * 100_000m;
                rate = new FeeRate(satPerByte);
            }

            _cache[blockTarget] = (rate, DateTimeOffset.UtcNow);
            return rate;
        }
        catch
        {
            // Return cached value if available, otherwise minimum
            if (_cache.TryGetValue(blockTarget, out var stale))
                return stale.Rate;
            return new FeeRate(1.0m);
        }
    }
}
