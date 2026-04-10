using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NBXplorer;
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
                    // Track in background — subscriptions are slow
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _tracker.TrackWalletAsync(strategy, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background tracking failed for {Strategy}", strategy);
                        }
                    });
                    // Return empty 200 — ExplorerClient.TrackAsync uses SendAsync<string>,
                    // which returns default(string) when ContentLength == 0.
                    // Returning JSON like {} would fail string deserialization.
                    return new HttpResponseMessage(HttpStatusCode.OK);
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

                var derivationSchemeStr = derivationScheme.ToString();

                // Store metadata first (fast DB writes, use request token)
                var rootedKeyPath = new RootedKeyPath(masterKey.Neuter().GetPublicKey().GetHDFingerPrint(), accountKeyPath);
                if (savePrivateKeys)
                {
                    await _tracker.SetMetadataAsync(derivationSchemeStr, "Mnemonic",
                        mnemonic.ToString(), cancellationToken);
                    await _tracker.SetMetadataAsync(derivationSchemeStr, "MasterHDKey",
                        masterKey.GetWif(nbNetwork).ToString(), cancellationToken);
                    await _tracker.SetMetadataAsync(derivationSchemeStr, "AccountHDKey",
                        accountKey.GetWif(nbNetwork).ToString(), cancellationToken);
                }
                await _tracker.SetMetadataAsync(derivationSchemeStr, "AccountKeyPath",
                    rootedKeyPath.ToString(), cancellationToken);
                await _tracker.SetMetadataAsync(derivationSchemeStr, "AccountDescriptor",
                    $"wpkh([{masterKey.Neuter().GetPublicKey().GetHDFingerPrint()}/{accountKeyPath}]{accountExtPubKey.GetWif(nbNetwork)})",
                    cancellationToken);
                await _tracker.SetMetadataAsync(derivationSchemeStr, "Birthdate",
                    DateTimeOffset.UtcNow.ToString("O"), cancellationToken);

                // Track the wallet in the background — address derivation + Electrum
                // subscriptions are slow (40+ network calls), so don't block the response
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _tracker.TrackWalletAsync(derivationSchemeStr, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background wallet tracking failed for {Strategy}", derivationSchemeStr);
                    }
                });

                var response = new GenerateWalletResponse
                {
                    Mnemonic = mnemonic.ToString(),
                    Passphrase = passphrase,
                    WordList = Wordlist.English,
                    WordCount = wordCount,
                    MasterHDKey = masterKey.GetWif(nbNetwork),
                    AccountHDKey = accountKey.GetWif(nbNetwork),
                    AccountKeyPath = rootedKeyPath,
                    AccountDescriptor = $"wpkh([{masterKey.Neuter().GetPublicKey().GetHDFingerPrint()}/{accountKeyPath}]{accountExtPubKey.GetWif(nbNetwork)})",
                    DerivationScheme = derivationScheme
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

            // GET /v1/cryptos/{code}/derivations/{scheme}/metadata/{key} — GetMetadata
            if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/metadata/[^/]+$"))
            {
                var strategy = ExtractStrategy(path);
                var key = ExtractMetadataKey(path);
                if (strategy != null && key != null)
                {
                    var value = await _tracker.GetMetadataAsync(strategy, key, cancellationToken);
                    if (value != null)
                        return OkResponse(value);
                }
                return NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/derivations/{scheme}/metadata/{key} — SetMetadata
            if (method == HttpMethod.Post && Regex.IsMatch(path, @"/derivations/[^/]+/metadata/[^/]+$"))
            {
                var strategy = ExtractStrategy(path);
                var key = ExtractMetadataKey(path);
                if (strategy != null && key != null)
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    // NBXplorer sends the value JSON-serialized, so deserialize it
                    var value = JsonConvert.DeserializeObject<string>(body);
                    await _tracker.SetMetadataAsync(strategy, key, value, cancellationToken);
                    return OkResponse(new { });
                }
                return NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/psbt/create — CreatePSBT
            if (method == HttpMethod.Post && path.EndsWith("/psbt/create"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var result = await HandleCreatePSBTAsync(strategy, body, cancellationToken);
                    return OkResponse(result);
                }
                return NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/psbt/update — UpdatePSBT
            if (method == HttpMethod.Post && path.EndsWith("/psbt/update"))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var result = HandleUpdatePSBT(body);
                return OkResponse(result);
            }

            // GET /v1/cryptos/{code}/derivations/{strategy}/utxos/scan — GetScanUTXOSetInformation
            if (method == HttpMethod.Get && path.EndsWith("/utxos/scan"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    var scanState = _tracker.GetScanState(strategy);
                    if (scanState != null)
                        return OkResponse(scanState);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/utxos/scan — ScanUTXOSetAsync
            if (method == HttpMethod.Post && path.EndsWith("/utxos/scan"))
            {
                var strategy = ExtractStrategy(path);
                if (strategy != null)
                {
                    var query = request.RequestUri?.Query ?? "";
                    var gapLimit = ExtractQueryInt(query, "gapLimit") ?? 10000;
                    var batchSize = ExtractQueryInt(query, "batchSize") ?? 3000;
                    var from = ExtractQueryInt(query, "from") ?? 0;
                    _tracker.StartScan(strategy, gapLimit, from, batchSize);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return NotFoundResponse();
            }

            // POST /v1/cryptos/{code}/derivations/{strategy}/utxos/wipe — WipeAsync
            if (method == HttpMethod.Post && path.EndsWith("/utxos/wipe"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
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

    private string ExtractMetadataKey(string path)
    {
        var match = Regex.Match(path, @"/metadata/([^/]+)$");
        if (match.Success)
            return Uri.UnescapeDataString(match.Groups[1].Value);
        return null;
    }

    private int? ExtractQueryInt(string query, string key)
    {
        var match = Regex.Match(query, $@"[?&]{key}=(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var val) ? val : null;
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

    private async Task<object> HandleCreatePSBTAsync(string strategyStr, string body, CancellationToken ct)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var nbNetwork = network.NBitcoinNetwork;
        var factory = new DerivationStrategyFactory(nbNetwork);
        var strategy = factory.Parse(strategyStr);
        var jsonSettings = network.NBXplorerNetwork.JsonSerializerSettings;

        var request = JsonConvert.DeserializeObject<CreatePSBTRequest>(body, jsonSettings);

        // Get available UTXOs
        var utxoChanges = await _tracker.GetUTXOChangesAsync(strategyStr, ct);
        var allUtxos = utxoChanges.Confirmed.UTXOs
            .Concat(utxoChanges.Unconfirmed.UTXOs)
            .Where(u => request.MinConfirmations <= 0 || u.Confirmations >= request.MinConfirmations)
            .Where(u => request.MinValue == null || (u.Value is Money m && m >= request.MinValue))
            .ToList();

        if (request.ExcludeOutpoints?.Count > 0)
            allUtxos = allUtxos.Where(u => !request.ExcludeOutpoints.Contains(u.Outpoint)).ToList();

        if (request.IncludeOnlyOutpoints?.Count > 0)
            allUtxos = allUtxos.Where(u => request.IncludeOnlyOutpoints.Contains(u.Outpoint)).ToList();

        // Get fee rate
        var feeRate = request.FeePreference?.ExplicitFeeRate;
        if (feeRate == null)
        {
            var blockTarget = request.FeePreference?.BlockTarget ?? 1;
            var feeResult = await _tracker.GetFeeRateAsync(blockTarget, ct);
            var feeJson = JObject.FromObject(feeResult);
            feeRate = feeJson["feeRate"]?.ToObject<FeeRate>() ?? new FeeRate(10m);
        }

        // Build transaction using NBitcoin's TransactionBuilder
        var txBuilder = nbNetwork.CreateTransactionBuilder();
        txBuilder.SetSigningOptions(SigHash.All);

        if (request.RBF == true || request.LockTime.HasValue)
            txBuilder.OptInRBF = true;

        // Add coins from UTXOs
        foreach (var utxo in allUtxos)
        {
            var coin = utxo.AsCoin(strategy);
            if (coin != null)
                txBuilder.AddCoins(coin);
        }

        // Add destinations
        var isSweep = false;
        foreach (var dest in request.Destinations ?? new List<CreatePSBTDestination>())
        {
            if (dest.SweepAll)
            {
                isSweep = true;
                txBuilder.SendAll(dest.Destination.ScriptPubKey);
            }
            else
            {
                txBuilder.Send(dest.Destination.ScriptPubKey, dest.Amount);
            }
        }

        // Set change address
        BitcoinAddress changeAddress = null;
        if (!isSweep)
        {
            if (request.ExplicitChangeAddress?.ScriptPubKey != null)
            {
                changeAddress = request.ExplicitChangeAddress.ScriptPubKey.GetDestinationAddress(nbNetwork);
            }
            else
            {
                var changeInfo = await _tracker.GetNextUnusedAddressAsync(strategyStr, true,
                    request.ReserveChangeAddress, ct);
                if (changeInfo != null)
                    changeAddress = BitcoinAddress.Create(changeInfo.Address.ToString(), nbNetwork);
            }

            if (changeAddress != null)
                txBuilder.SetChange(changeAddress);
        }

        txBuilder.SendEstimatedFees(feeRate);

        var psbt = txBuilder.BuildPSBT(false);

        // Add HD key path info to PSBT inputs
        foreach (var input in psbt.Inputs)
        {
            var utxo = allUtxos.FirstOrDefault(u => u.Outpoint == input.PrevOut);
            if (utxo?.KeyPath != null)
            {
                var feature = utxo.KeyPath.Indexes[0] == 1
                    ? DerivationFeature.Change : DerivationFeature.Deposit;
                var line = strategy.GetLineFor(feature);
                var derivation = line.Derive(utxo.KeyPath.Indexes.Last());
                var pubKeys = strategy.GetExtPubKeys().ToArray();
                foreach (var pubKey in pubKeys)
                {
                    var derived = pubKey.Derive(utxo.KeyPath);
                    input.AddKeyPath(derived.GetPublicKey(),
                        new RootedKeyPath(pubKey.GetPublicKey().GetHDFingerPrint(), utxo.KeyPath));
                }
            }
        }

        var response = new JObject
        {
            ["psbt"] = psbt.ToBase64(),
            ["changeAddress"] = changeAddress?.ToString()
        };

        return response;
    }

    private object HandleUpdatePSBT(string body)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var jsonSettings = network.NBXplorerNetwork.JsonSerializerSettings;

        var request = JsonConvert.DeserializeObject<UpdatePSBTRequest>(body, jsonSettings);

        var psbt = request.PSBT;

        // Rebase key paths if requested
        if (request.RebaseKeyPaths?.Count > 0)
        {
            foreach (var rebase in request.RebaseKeyPaths)
            {
                psbt.RebaseKeyPaths(rebase.AccountKey, rebase.AccountKeyPath);
            }
        }

        return new JObject { ["psbt"] = psbt.ToBase64() };
    }

    private HttpResponseMessage OkResponse(object data)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var jsonSettings = network.NBXplorerNetwork.JsonSerializerSettings;
        var json = JsonConvert.SerializeObject(data, jsonSettings);
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
