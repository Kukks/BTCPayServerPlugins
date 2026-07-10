#nullable enable
using System;
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
        try
        {
            // Resolve once here (at config/client-creation time) to decide capability up front.
            resolved = LNURLResolver.Resolve(value, network, http, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }

        return new LNURLVerifyLightningClient(resolved, network, http, _loggerFactory);
    }
}
