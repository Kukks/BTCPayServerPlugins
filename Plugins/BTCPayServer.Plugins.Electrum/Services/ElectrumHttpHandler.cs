using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Intercepts ExplorerClient HTTP requests and routes them to the Electrum engine.
/// This allows BTCPayWallet to work unmodified — it calls ExplorerClient methods which
/// make HTTP requests, and this handler returns NBXplorer-compatible JSON responses
/// built from our Electrum backend.
/// </summary>
public class ElectrumHttpHandler : HttpMessageHandler
{
    private readonly ElectrumWalletTracker _tracker;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<ElectrumHttpHandler> _logger;

    public ElectrumHttpHandler(
        ElectrumWalletTracker tracker,
        BTCPayNetworkProvider networkProvider,
        ILogger<ElectrumHttpHandler> logger)
    {
        _tracker = tracker;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var method = request.Method;

        _logger.LogDebug("Intercepting {Method} {Path}", method, path);

        try
        {
            // POST /v1/cryptos/{code}/derivations — GenerateWallet or Track
            if (method == HttpMethod.Post && Regex.IsMatch(path, @"/v1/cryptos/\w+/derivations(/[^/]+)?$"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    // Track existing wallet
                    await _tracker.TrackWalletAsync(strategy, cancellationToken);
                    return OkResponse(new { });
                }

                // Generate new wallet
                var cryptoCode = ExtractCryptoCode(path);
                var network = _networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode ?? "BTC");
                var nbNetwork = network.NBitcoinNetwork;

                var body = request.Content != null
                    ? await request.Content.ReadAsStringAsync(cancellationToken)
                    : "{}";
                var genRequest = JsonConvert.DeserializeObject<JObject>(body) ?? new JObject();

                var existingMnemonic = genRequest["ExistingMnemonic"]?.Value<string>();
                var passphrase = genRequest["Passphrase"]?.Value<string>() ?? "";
                var accountNumber = genRequest["AccountNumber"]?.Value<int>() ?? 0;
                var scriptPubKeyTypeStr = genRequest["ScriptPubKeyType"]?.Value<string>();
                var wordCountVal = genRequest["WordCount"]?.Value<int?>();
                var savePrivateKeys = genRequest["SavePrivateKeys"]?.Value<bool>() ?? false;

                var wordCount = wordCountVal switch
                {
                    15 => NBitcoin.WordCount.Fifteen,
                    18 => NBitcoin.WordCount.Eighteen,
                    21 => NBitcoin.WordCount.TwentyOne,
                    24 => NBitcoin.WordCount.TwentyFour,
                    _ => NBitcoin.WordCount.Twelve
                };

                var mnemonic = string.IsNullOrEmpty(existingMnemonic)
                    ? new Mnemonic(Wordlist.English, wordCount)
                    : new Mnemonic(existingMnemonic);

                var masterKey = mnemonic.DeriveExtKey(passphrase);

                var scriptPubKeyType = scriptPubKeyTypeStr?.ToLowerInvariant() switch
                {
                    "segwit" => ScriptPubKeyType.Segwit,
                    "segwitp2sh" => ScriptPubKeyType.SegwitP2SH,
                    "legacy" => ScriptPubKeyType.Legacy,
                    "taprootbip86" => ScriptPubKeyType.TaprootBIP86,
                    _ => ScriptPubKeyType.Segwit
                };

                var accountKeyPath = GetAccountKeyPath(scriptPubKeyType, nbNetwork, accountNumber);
                var accountKey = masterKey.Derive(accountKeyPath);
                var accountExtPubKey = accountKey.Neuter();

                var factory = new DerivationStrategyFactory(nbNetwork);
                var derivationScheme = factory.CreateDirectDerivationStrategy(accountExtPubKey,
                    new DerivationStrategyOptions { ScriptPubKeyType = scriptPubKeyType });

                // Track the wallet
                await _tracker.TrackWalletAsync(derivationScheme.ToString(), cancellationToken);

                var response = new JObject
                {
                    ["mnemonic"] = mnemonic.ToString(),
                    ["passphrase"] = passphrase,
                    ["wordList"] = "English",
                    ["wordCount"] = (int)wordCount,
                    ["masterHDKey"] = masterKey.GetWif(nbNetwork).ToString(),
                    ["accountHDKey"] = accountKey.GetWif(nbNetwork).ToString(),
                    ["accountKeyPath"] = new RootedKeyPath(masterKey.Neuter().GetPublicKey().GetHDFingerPrint(), accountKeyPath).ToString(),
                    ["accountDescriptor"] = $"wpkh([{masterKey.Neuter().GetPublicKey().GetHDFingerPrint()}/{accountKeyPath}]{accountExtPubKey})",
                    ["derivationScheme"] = derivationScheme.ToString()
                };

                return OkResponse(response);
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/addresses/unused — GetUnused
            if (method == HttpMethod.Get && path.Contains("/addresses/unused"))
            {
                var strategy = ExtractStrategy(path);
                var query = request.RequestUri?.Query ?? "";
                var isChange = query.Contains("feature=Change", StringComparison.OrdinalIgnoreCase);
                var reserve = query.Contains("reserve=True", StringComparison.OrdinalIgnoreCase) ||
                              query.Contains("reserve=true", StringComparison.OrdinalIgnoreCase);
                if (strategy != null)
                {
                    var result = await _tracker.GetNextUnusedAddressAsync(strategy, isChange, reserve, cancellationToken);
                    if (result != null)
                    {
                        return OkResponse(result);
                    }
                }
                return NotFoundResponse();
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/utxos — GetUTXOs
            if (method == HttpMethod.Get && path.EndsWith("/utxos"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    var result = await _tracker.GetUTXOChangesAsync(strategy, cancellationToken);
                    return OkResponse(result);
                }
                return NotFoundResponse();
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/balance — GetBalance
            if (method == HttpMethod.Get && path.EndsWith("/balance"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    var result = await _tracker.GetBalanceAsync(strategy, cancellationToken);
                    return OkResponse(result);
                }
                return NotFoundResponse();
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/transactions/{txId} — GetTransaction by strategy + txid
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/transactions/[0-9a-fA-F]{64}$"))
            {
                var strategy = ExtractStrategy(path);
                var txId = ExtractTxId(path);
                if (strategy != null && txId != null)
                {
                    var result = await _tracker.GetTransactionInfoAsync(strategy, txId, cancellationToken);
                    if (result != null)
                        return OkResponse(result);
                }
                return NotFoundResponse();
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/transactions — GetTransactions
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/transactions$"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    var result = await _tracker.GetTransactionsResponseAsync(strategy, cancellationToken);
                    return OkResponse(result);
                }
                return NotFoundResponse();
            }

            // GET /v1/cryptos/{code}/transactions/{txId} — GetTransaction by txid only
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/v1/cryptos/\w+/transactions/[0-9a-fA-F]{64}$"))
            {
                var txId = ExtractTxId(path);
                if (txId != null)
                {
                    var result = await _tracker.GetTransactionResultAsync(txId, cancellationToken);
                    if (result != null)
                        return OkResponse(result);
                }
                return NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/transactions — Broadcast
            if (method == HttpMethod.Post && Regex.IsMatch(path, @"/v1/cryptos/\w+/transactions$"))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = await _tracker.BroadcastAsync(body, cancellationToken);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/fees/{blockTarget} — GetFeeRate
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/v1/cryptos/\w+/fees/\d+$"))
            {
                var blockTarget = int.Parse(path.Split('/').Last());
                var feeRate = await _tracker.GetFeeRateAsync(blockTarget, cancellationToken);
                return OkResponse(feeRate);
            }

            // GET /v1/cryptos/{code}/status — GetStatus
            if (method == HttpMethod.Get && path.EndsWith("/status"))
            {
                var status = _tracker.GetStatus();
                return OkResponse(status);
            }

            _logger.LogWarning("Unhandled ExplorerClient request: {Method} {Path}", method, path);
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Method} {Path}", method, path);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(ex.Message)
            };
        }
    }

    private string ExtractStrategy(string path)
    {
        var match = Regex.Match(path, @"/derivations/([^/]+)");
        if (match.Success)
            return Uri.UnescapeDataString(match.Groups[1].Value);
        return null;
    }

    private string ExtractCryptoCode(string path)
    {
        var match = Regex.Match(path, @"/v1/cryptos/(\w+)/");
        if (match.Success)
            return match.Groups[1].Value;
        return null;
    }

    private string ExtractTxId(string path)
    {
        var match = Regex.Match(path, @"/transactions/([0-9a-fA-F]{64})");
        if (match.Success)
            return match.Groups[1].Value;
        return null;
    }

    private static KeyPath GetAccountKeyPath(ScriptPubKeyType scriptPubKeyType, Network network, int accountNumber)
    {
        var coinType = network.ChainName == ChainName.Mainnet ? 0 : 1;
        var purpose = scriptPubKeyType switch
        {
            ScriptPubKeyType.Legacy => 44,
            ScriptPubKeyType.SegwitP2SH => 49,
            ScriptPubKeyType.Segwit => 84,
            ScriptPubKeyType.TaprootBIP86 => 86,
            _ => 84
        };
        return new KeyPath($"{purpose}'/{coinType}'/{accountNumber}'");
    }

    private HttpResponseMessage OkResponse(object data)
    {
        var json = JsonConvert.SerializeObject(data, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private HttpResponseMessage NotFoundResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
