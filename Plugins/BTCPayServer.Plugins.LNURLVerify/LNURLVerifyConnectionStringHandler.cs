#nullable enable
using System;
using System.Net.Http;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.LNURLVerify;

public class LNURLVerifyConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

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

        // Full construction (resolve + build client + register poller factory) lands in Task 8.
        error = "not yet implemented";
        return null;
    }
}
