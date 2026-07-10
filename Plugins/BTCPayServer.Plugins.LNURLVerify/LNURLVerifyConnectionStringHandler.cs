#nullable enable
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LNURLVerifyConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    // BTCPay's LightningClientFactory does NOT cache clients — it calls this handler's Create on every
    // poll/listen/operation (e.g. LightningListener.PollPayment). Resolving over the network each time
    // would hammer the LNURL server and block a thread per call, so cache the resolution briefly. The
    // withdraw's mutable bits (k1/balance) are refreshed at send time regardless, so a short TTL is safe.
    private static readonly ConcurrentDictionary<string, (ResolvedLnurl Resolved, DateTimeOffset Expiry)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public LNURLVerifyConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "lnurl")
        {
            error = null;
            return null;
        }

        if (!kv.TryGetValue("value", out var value) || string.IsNullOrWhiteSpace(value))
        {
            error = "The key 'value' (an LNURL or Lightning address) is mandatory for lnurl connection strings";
            return null;
        }

        error = null;
        var http = _httpClientFactory.CreateClient(nameof(LNURLVerifyConnectionStringHandler));
        // Bound each LNURL request rather than inheriting the default 100s HttpClient timeout.
        http.Timeout = TimeSpan.FromSeconds(30);

        ResolvedLnurl resolved;
        if (_cache.TryGetValue(value, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
        {
            resolved = cached.Resolved;
        }
        else
        {
            try
            {
                // Resolve (network) to decide capability up front; cached so the frequent per-poll
                // Create calls don't re-fetch. Failures are not cached, so they retry next time.
                resolved = LNURLResolver.Resolve(value, network, http, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            _cache[value] = (resolved, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        return new LNURLVerifyLightningClient(resolved, network, http, _loggerFactory);
    }
}
