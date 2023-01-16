using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments.PayJoin;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json.Linq;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;


public class BTCPayWallet : IWallet
{
    public OnChainPaymentMethodData OnChainPaymentMethodData;
    public readonly DerivationStrategyBase DerivationScheme;
    public readonly ExplorerClient ExplorerClient;
    public readonly IBTCPayServerClientFactory BtcPayServerClientFactory;
    public WabisabiStoreSettings WabisabiStoreSettings;
    public readonly IUTXOLocker UtxoLocker;
    public readonly ILogger Logger;
    private static readonly BlockchainAnalyzer BlockchainAnalyzer = new();

    public BTCPayWallet(OnChainPaymentMethodData onChainPaymentMethodData, DerivationStrategyBase derivationScheme,
        ExplorerClient explorerClient, BTCPayKeyChain keyChain,
        IDestinationProvider destinationProvider, IBTCPayServerClientFactory btcPayServerClientFactory, string storeId,
        WabisabiStoreSettings wabisabiStoreSettings, IUTXOLocker utxoLocker,
        ILoggerFactory loggerFactory, Smartifier smartifier,
        ConcurrentDictionary<string, Dictionary<OutPoint, DateTimeOffset>> bannedCoins)
    {
        KeyChain = keyChain;
        DestinationProvider = destinationProvider;
        OnChainPaymentMethodData = onChainPaymentMethodData;
        DerivationScheme = derivationScheme;
        ExplorerClient = explorerClient;
        BtcPayServerClientFactory = btcPayServerClientFactory;
        StoreId = storeId;
        WabisabiStoreSettings = wabisabiStoreSettings;
        UtxoLocker = utxoLocker;
        _smartifier = smartifier;
        _bannedCoins = bannedCoins;
        Logger = loggerFactory.CreateLogger($"BTCPayWallet_{storeId}");
    }

    public string StoreId { get; set; }

    public string WalletName => StoreId;
    public bool IsUnderPlebStop => false;

    bool IWallet.IsMixable(string coordinator)
    {
        return OnChainPaymentMethodData?.Enabled is  true && WabisabiStoreSettings.Settings.SingleOrDefault(settings =>
            settings.Coordinator.Equals(coordinator))?.Enabled is  true && ((BTCPayKeyChain)KeyChain).KeysAvailable;
    }

    public IKeyChain KeyChain { get; }
    public IDestinationProvider DestinationProvider { get; }

    public int AnonymitySetTarget => WabisabiStoreSettings.PlebMode? 2:  WabisabiStoreSettings.AnonymitySetTarget;
    public bool ConsolidationMode => !WabisabiStoreSettings.PlebMode && WabisabiStoreSettings.ConsolidationMode;
    public TimeSpan FeeRateMedianTimeFrame { get; } = TimeSpan.FromHours(KeyManager.DefaultFeeRateMedianTimeFrameHours);
    public bool RedCoinIsolation => !WabisabiStoreSettings.PlebMode &&WabisabiStoreSettings.RedCoinIsolation;
    public bool BatchPayments => WabisabiStoreSettings.PlebMode || WabisabiStoreSettings.BatchPayments;

    public async Task<bool> IsWalletPrivateAsync()
    {
      return !BatchPayments && await GetPrivacyPercentageAsync()>= 1;
    }

    public async Task<double> GetPrivacyPercentageAsync()
    {
        return GetPrivacyPercentage(await GetAllCoins(), AnonymitySetTarget);
    }

    public async Task<CoinsView> GetAllCoins()
    {
        await _savingProgress;
        var client = await BtcPayServerClientFactory.Create(null, StoreId);
        var utxos = await client.GetOnChainWalletUTXOs(StoreId, "BTC");
        await _smartifier.LoadCoins(utxos.ToList());
        var coins = await Task.WhenAll(_smartifier.Coins.Where(pair => utxos.Any(data => data.Outpoint == pair.Key))
            .Select(pair => pair.Value));

        return new CoinsView(coins);
    }

    public double GetPrivacyPercentage(CoinsView coins, int privateThreshold)
    {
        var privateAmount = coins.FilterBy(x => x.HdPubKey.AnonymitySet >= privateThreshold).TotalAmount();
        var normalAmount = coins.FilterBy(x => x.HdPubKey.AnonymitySet < privateThreshold).TotalAmount();

        var privateDecimalAmount = privateAmount.ToDecimal(MoneyUnit.BTC);
        var normalDecimalAmount = normalAmount.ToDecimal(MoneyUnit.BTC);
        var totalDecimalAmount = privateDecimalAmount + normalDecimalAmount;

        var pcPrivate = totalDecimalAmount == 0M ? 1d : (double)(privateDecimalAmount / totalDecimalAmount);
        return pcPrivate;
    }
    
    private IRoundCoinSelector _coinSelector;
    public readonly Smartifier _smartifier;
    private readonly ConcurrentDictionary<string, Dictionary<OutPoint, DateTimeOffset>> _bannedCoins;

    public IRoundCoinSelector GetCoinSelector()
    {
        _coinSelector??= new BTCPayCoinjoinCoinSelector(this,  Logger );
        return _coinSelector;
    }

    public async Task<IEnumerable<SmartCoin>> GetCoinjoinCoinCandidatesAsync(string coordinatorName)
    {
        try
        {
            await _savingProgress;
        }
        catch (Exception e)
        {
        }
        try
        {
            if (IsUnderPlebStop)
            {
                return Array.Empty<SmartCoin>();
            }

            var client = await BtcPayServerClientFactory.Create(null, StoreId);
            var utxos = await client.GetOnChainWalletUTXOs(StoreId, "BTC");
            var objs = client.GetOnChainWalletObjects(StoreId, "BTC",
                new GetWalletObjectsRequest()
                {
                    Type = "utxo", Ids = utxos.Select(data => data.Outpoint.ToString()).ToArray()
                });
            if (!WabisabiStoreSettings.PlebMode)
            {
                if (WabisabiStoreSettings.InputLabelsAllowed?.Any() is true)
                {
                    utxos = utxos.Where(data =>
                        !WabisabiStoreSettings.InputLabelsAllowed.Any(s => data.Labels.ContainsKey(s)));
                }

                if (WabisabiStoreSettings.InputLabelsExcluded?.Any() is true)
                {
                    utxos = utxos.Where(data =>
                        WabisabiStoreSettings.InputLabelsExcluded.All(s => !data.Labels.ContainsKey(s)));
                }
            }

            var locks = await UtxoLocker.FindLocks(utxos.Select(data => data.Outpoint).ToArray());
            utxos = utxos.Where(data => !locks.Contains(data.Outpoint)).Where(data => data.Confirmations > 0);
            if (_bannedCoins.TryGetValue(coordinatorName, out var bannedCoins))
            {
                var expired = bannedCoins.Where(pair => pair.Value < DateTimeOffset.Now).ToArray();
                foreach (var c in expired)
                {
                    bannedCoins.Remove(c.Key);

                }

                utxos = utxos.Where(data => !bannedCoins.ContainsKey(data.Outpoint));
            }
            await _smartifier.LoadCoins(utxos.Where(data => data.Confirmations>0).ToList());
            
            var resultX =  await Task.WhenAll(_smartifier.Coins.Where(pair =>  utxos.Any(data => data.Outpoint == pair.Key))
                .Select(pair => pair.Value));

            foreach (SmartCoin c in resultX)
            {
                var utxo = utxos.Single(coin => coin.Outpoint == c.Outpoint);
                c.Height = new Height((uint) utxo.Confirmations);
            }
            
            return resultX;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not compute coin candidate");
            return Array.Empty<SmartCoin>();
        }
    }


    public async Task<IEnumerable<SmartTransaction>> GetTransactionsAsync()
    {
        return Array.Empty<SmartTransaction>();

    }


    public class CoinjoinData
    {
        public class CoinjoinDataCoin
        {
            public string Outpoint { get; set; }
            public decimal Amount { get; set; }
            public double AnonymitySet { get; set; }
            public string? PayoutId { get; set; }
        }
        public string Round { get; set; }
        public string CoordinatorName { get; set; }
        public string Transaction { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
        public CoinjoinDataCoin[] CoinsIn { get; set; } = Array.Empty<CoinjoinDataCoin>();
        public CoinjoinDataCoin[] CoinsOut { get; set; }= Array.Empty<CoinjoinDataCoin>();
    }

    private Task _savingProgress = Task.CompletedTask;

    public async Task RegisterCoinjoinTransaction(CoinJoinResult result, string coordinatorName)
    {
        _savingProgress = RegisterCoinjoinTransactionInternal(result, coordinatorName);
        await _savingProgress;
    }
    private async Task RegisterCoinjoinTransactionInternal(CoinJoinResult result, string coordinatorName)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Logger.LogInformation($"Registering coinjoin result for {StoreId}");
            
            var storeIdForutxo = WabisabiStoreSettings.PlebMode ||
                string.IsNullOrEmpty(WabisabiStoreSettings.MixToOtherWallet)? StoreId: WabisabiStoreSettings.MixToOtherWallet;
            var client = await BtcPayServerClientFactory.Create(null, StoreId);
            BTCPayServerClient utxoClient = client;
            DerivationStrategyBase utxoDerivationScheme = DerivationScheme;
            if (storeIdForutxo != StoreId)
            {
                utxoClient = await BtcPayServerClientFactory.Create(null, storeIdForutxo);
                var pm  = await utxoClient.GetStoreOnChainPaymentMethod(storeIdForutxo, "BTC");
                utxoDerivationScheme = ExplorerClient.Network.DerivationStrategyFactory.Parse(pm.DerivationScheme);
            }
            var kp = await ExplorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
                WellknownMetadataKeys.AccountKeyPath);
            
            //mark the tx as a coinjoin at a specific coordinator
            var txObject = new AddOnChainWalletObjectRequest() {Id = result.UnsignedCoinJoin.GetHash().ToString(), Type = "tx"};

            var labels = new[]
            {
                new AddOnChainWalletObjectRequest() {Id = "coinjoin", Type = "label"},
                new AddOnChainWalletObjectRequest() {Id = coordinatorName, Type = "label"}
            };


            
            await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", txObject);
            if(storeIdForutxo != StoreId)
                await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", txObject);
            
            foreach (var label in labels)
            {
                await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", label);
                await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject, new AddOnChainWalletObjectLinkRequest()
                {
                    Id = label.Id,
                    Type = label.Type
                }, CancellationToken.None);

                if (storeIdForutxo != StoreId)
                {await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", label);
                    await utxoClient.AddOrUpdateOnChainWalletLink(storeIdForutxo, "BTC", txObject, new AddOnChainWalletObjectLinkRequest()
                    {
                        Id = label.Id,
                        Type = label.Type
                    }, CancellationToken.None);
                }
            }

            List<(IndexedTxOut txout, Task<KeyPathInformation>)> scriptInfos = new();
            var payoutLabels = 
            result.HandledPayments.Select(pair =>
                new AddOnChainWalletObjectRequest() {Id = pair.Value.Identifier, Type = "payout"});

            if (payoutLabels.Any())
            {

                await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC",
                    new AddOnChainWalletObjectRequest("label", "payout"));
                await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject,
                    new AddOnChainWalletObjectLinkRequest("label", "payout"));


                foreach (var label in payoutLabels)
                {

                    await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", label);
                    await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject,
                        new AddOnChainWalletObjectLinkRequest() {Id = label.Id, Type = label.Type},
                        CancellationToken.None);

                    await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", label,
                        new AddOnChainWalletObjectLinkRequest() {Id = "payout", Type = "label"},
                        CancellationToken.None);
                }
            }

            Dictionary<IndexedTxOut, PendingPayment> indexToPayment = new();
            foreach (var script in result.RegisteredOutputs)
            {
                var txout = result.UnsignedCoinJoin.Outputs.AsIndexedOutputs()
                    .Single(@out => @out.TxOut.ScriptPubKey == script);

                
                //this was not a mix to self, but rather a payment
                var isPayment = result.HandledPayments.Where(pair =>
                    pair.Key.ScriptPubKey == txout.TxOut.ScriptPubKey && pair.Key.Value == txout.TxOut.Value);
                if (isPayment.Any())
                {
                    indexToPayment.Add(txout, isPayment.First().Value);
                   continue;
                }

                scriptInfos.Add((txout, ExplorerClient.GetKeyInformationAsync(BlockchainAnalyzer.StdDenoms.Contains(txout.TxOut.Value)?utxoDerivationScheme:DerivationScheme, script)));
            }

            await Task.WhenAll(scriptInfos.Select(t => t.Item2));
            var scriptInfos2 = scriptInfos.Where(tuple => tuple.Item2.Result is not null).ToDictionary(tuple => tuple.txout.TxOut.ScriptPubKey);
            var smartTx = new SmartTransaction(result.UnsignedCoinJoin, new Height(HeightType.Unknown));
            result.RegisteredCoins.ForEach(coin =>
            {
                coin.HdPubKey.SetKeyState(KeyState.Used);
                coin.SpenderTransaction = smartTx;
                smartTx.TryAddWalletInput(coin);
            });
            result.RegisteredOutputs.ForEach(s =>
            {
                if (scriptInfos2.TryGetValue(s, out var si))
                {
                    var derivation = DerivationScheme.GetChild(si.Item2.Result.KeyPath).GetExtPubKeys().First()
                        .PubKey;
                    var hdPubKey = new HdPubKey(derivation, kp.Derive(si.Item2.Result.KeyPath).KeyPath,
                        SmartLabel.Empty,
                        KeyState.Used);
                    
                    var coin = new SmartCoin(smartTx, si.txout.N, hdPubKey);
                    smartTx.TryAddWalletOutput(coin);
                }
            });

            //
            // scriptInfos.ForEach(information =>
            // {
            //     var derivation = DerivationScheme.GetChild(information.Item2.Result.KeyPath).GetExtPubKeys().First()
            //         .PubKey;
            //     var hdPubKey = new HdPubKey(derivation, kp.Derive(information.Item2.Result.KeyPath).KeyPath,
            //         SmartLabel.Empty,
            //         KeyState.Used);
            //
            //     var coin = new SmartCoin(smartTx, information.txout.N, hdPubKey);
            //     smartTx.TryAddWalletOutput(coin);
            // });
            
            
            try
            {
                BlockchainAnalyzer.Analyze(smartTx);
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to analyze anonsets of tx {smartTx.GetHash()}");
            }


            foreach (SmartCoin smartTxWalletOutput in smartTx.WalletOutputs)
                {
                    var utxoObject = new AddOnChainWalletObjectRequest()
                    {
                        Id = smartTxWalletOutput.Outpoint.ToString(),
                        Type = "utxo"
                    };
                    if (BlockchainAnalyzer.StdDenoms.Contains(smartTxWalletOutput.TxOut.Value.Satoshi) && smartTxWalletOutput.AnonymitySet != 1)
                    {
                        
                        await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", new AddOnChainWalletObjectRequest( "utxo", smartTxWalletOutput.Outpoint.ToString())
                        {
                            Data = JObject.FromObject(new
                            {
                                smartTxWalletOutput.AnonymitySet
                            })
                        });
                        await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", new AddOnChainWalletObjectRequest( "label", $"anonset-{smartTxWalletOutput.AnonymitySet}"));

                        if (smartTxWalletOutput.AnonymitySet != 1)
                        {
                            await utxoClient.AddOrUpdateOnChainWalletLink(storeIdForutxo, "BTC", utxoObject, 
                                new AddOnChainWalletObjectLinkRequest() {Id =  $"anonset-{smartTxWalletOutput.AnonymitySet}", Type = "label"}, CancellationToken.None);

                        }
                    }
                }
                await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC",
                    new AddOnChainWalletObjectRequest()
                    {
                        Id = result.RoundId.ToString(),
                        Type = "coinjoin",
                        Data = JObject.FromObject(
                            new CoinjoinData()
                            {
                                Round = result.RoundId.ToString(),
                                CoordinatorName = coordinatorName,
                                Transaction = result.UnsignedCoinJoin.GetHash().ToString(),
                                CoinsIn =   smartTx.WalletInputs.Select(coin => new CoinjoinData.CoinjoinDataCoin()
                                {
                                    AnonymitySet = coin.AnonymitySet,
                                    PayoutId =  null,
                                    Amount = coin.Amount.ToDecimal(MoneyUnit.BTC),
                                    Outpoint = coin.Outpoint.ToString()
                                }).ToArray(),
                                CoinsOut =   smartTx.WalletOutputs.Select(coin => new CoinjoinData.CoinjoinDataCoin()
                                {
                                    AnonymitySet = coin.AnonymitySet,
                                    PayoutId =  null,
                                    Amount = coin.Amount.ToDecimal(MoneyUnit.BTC),
                                    Outpoint = coin.Outpoint.ToString()
                                }).Concat(indexToPayment.Select(pair => new CoinjoinData.CoinjoinDataCoin()
                                {
                                    Amount = pair.Key.TxOut.Value.ToDecimal(MoneyUnit.BTC),
                                    PayoutId = pair.Value.Identifier,
                                    Outpoint = new OutPoint(result.UnsignedCoinJoin, pair.Key.N).ToString()
                                })).ToArray()
                            })
                    });
                
                await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject,
                    new AddOnChainWalletObjectLinkRequest() {Id = result.RoundId.ToString(), Type = "coinjoin"},
                    CancellationToken.None);
                stopwatch.Stop();
                
                Logger.LogInformation($"Registered coinjoin result for {StoreId} in {stopwatch.Elapsed}");

        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not save coinjoin progress!");
            // ignored
        }
    }


    public async Task UnlockUTXOs()
    {
        var client = await BtcPayServerClientFactory.Create(null, StoreId);
        var utxos = await client.GetOnChainWalletUTXOs(StoreId, "BTC");
        var unlocked = new List<string>();
        foreach (OnChainWalletUTXOData utxo in utxos)
        {

            if (await UtxoLocker.TryUnlock(utxo.Outpoint))
            {
                unlocked.Add(utxo.Outpoint.ToString());
            }
        }

        Logger.LogInformation($"unlocked utxos: {string.Join(',', unlocked)}");
    }

}
