#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Blink;

public class BlinkLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public BlinkLightningConnectionStringHandler(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Returns the default Blink GraphQL server for the given network when none is specified.</summary>
    internal static string DefaultServer(Network network)
        => network.Name switch
        {
            nameof(Network.TestNet) => "https://api.staging.galoy.io/graphql",
            nameof(Network.RegTest) => "http://localhost:4455/graphql",
            _ => "https://api.blink.sv/graphql"
        };

    /// <summary>A blink connection string denotes a non-custodial (receive-only) account when it has
    /// no <c>api-key</c> and carries an <c>ln-address</c> (or its <c>username</c> alias).</summary>
    internal static bool IsLnAddressConnectionString(
        System.Collections.Generic.Dictionary<string, string> kv, out string? lnAddress)
    {
        lnAddress = null;
        if (kv.ContainsKey("api-key"))
            return false;
        return kv.TryGetValue("ln-address", out lnAddress) || kv.TryGetValue("username", out lnAddress);
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "blink")
        {
            error = null;
            return null;
        }

        // Non-custodial (Spark) accounts: identified by an "ln-address" (or bare "username") key
        // and the absence of an "api-key". These have no GraphQL wallet-id and can only receive,
        // via LNURL-pay + LUD-21 verify brokered by blink-lnurl-server.
        if (IsLnAddressConnectionString(kv, out var lnAddress))
        {
            return CreateLnAddressClient(lnAddress, kv, network, out error);
        }

        if (!kv.TryGetValue("server", out var server))
        {
            server = DefaultServer(network);
            // error = $"The key 'server' is mandatory for blink connection strings";
            // return null;
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
            || uri.Scheme != "http" && uri.Scheme != "https")
        {
            error = "The key 'server' should be an URI starting by http:// or https://";
            return null;
        }

        bool allowInsecure = false;
        if (kv.TryGetValue("allowinsecure", out var allowinsecureStr))
        {
            var allowedValues = new[] {"true", "false"};
            if (!allowedValues.Any(v => v.Equals(allowinsecureStr, StringComparison.OrdinalIgnoreCase)))
            {
                error = "The key 'allowinsecure' should be true or false";
                return null;
            }

            allowInsecure = allowinsecureStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (!LightningConnectionStringHelper.VerifySecureEndpoint(uri, allowInsecure))
        {
            error = "The key 'allowinsecure' is false, but server's Uri is not using https";
            return null;
        }

        if (!kv.TryGetValue("api-key", out var apiKey))
        {
            error = "The key 'api-key' is not found";
            return null;
        }

        error = null;

        var client = _httpClientFactory.CreateClient();

        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        client.BaseAddress = uri;

        kv.TryGetValue("wallet-id", out var walletId);

        BlinkCurrency? currency = null;
        if (kv.TryGetValue("currency", out var v))
        {
            try
            {
                currency = BlinkLightningClient.ParseBlinkCurrency(v);
            }
            catch (FormatException e)
            {
                error = e.Message;
                return null;
            }
        }
        return new BlinkLightningClient(apiKey, uri, walletId, currency, network, client, _loggerFactory.CreateLogger(nameof(BlinkLightningClient)));
    }

    private ILightningClient? CreateLnAddressClient(string? lnAddress,
        System.Collections.Generic.Dictionary<string, string> kv, Network network, out string? error)
    {
        if (string.IsNullOrWhiteSpace(lnAddress))
        {
            error = "The key 'ln-address' must be a Blink lightning address (e.g. user@blink.sv)";
            return null;
        }

        // Allow a bare username; default the domain to blink.sv.
        if (!lnAddress.Contains('@'))
            lnAddress = $"{lnAddress}@blink.sv";

        var atParts = lnAddress.Split('@');
        if (atParts.Length != 2 || string.IsNullOrWhiteSpace(atParts[0]) || string.IsNullOrWhiteSpace(atParts[1]))
        {
            error = $"The key 'ln-address' is not a valid lightning address ('{lnAddress}')";
            return null;
        }

        // USDB (non-custodial Dollar balance) is requested via currency=USD. Not yet live server-side;
        // passed through so it works automatically once Blink ships Spark USD receive support.
        bool usd = false;
        if (kv.TryGetValue("currency", out var currencyStr))
        {
            try
            {
                usd = BlinkLightningClient.ParseBlinkCurrency(currencyStr) == BlinkCurrency.USD;
            }
            catch (FormatException e)
            {
                error = e.Message;
                return null;
            }
        }

        error = null;
        var client = _httpClientFactory.CreateClient();
        // Bound each LNURL/verify request rather than inheriting the default 100s HttpClient timeout,
        // so a degraded blink-lnurl-server cannot tie up a connection per poll for that long.
        client.Timeout = TimeSpan.FromSeconds(30);
        return new BlinkLnAddressLightningClient(lnAddress, usd, network, client,
            _loggerFactory.CreateLogger(nameof(BlinkLnAddressLightningClient)));
    }
}