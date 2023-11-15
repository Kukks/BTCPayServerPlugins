using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using LinqKit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;


public class BTCPayWallet : IWallet, IDestinationProvider 
{
    private readonly WalletRepository _walletRepository;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly BitcoinLikePayoutHandler _bitcoinLikePayoutHandler;
    private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
    private readonly Services.Wallets.BTCPayWallet _btcPayWallet;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    // public OnChainPaymentMethodData OnChainPaymentMethodData;
    public readonly DerivationStrategyBase DerivationScheme;
    public readonly ExplorerClient ExplorerClient;
    // public readonly IBTCPayServerClientFactory BtcPayServerClientFactory;
    public WabisabiStoreSettings WabisabiStoreSettings;
    public readonly IUTXOLocker UtxoLocker;
    public readonly ILogger Logger;
    public static readonly BlockchainAnalyzer BlockchainAnalyzer = new();

    public BTCPayWallet(
        WalletRepository walletRepository,
        BTCPayNetworkProvider btcPayNetworkProvider,
        BitcoinLikePayoutHandler bitcoinLikePayoutHandler,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
        Services.Wallets.BTCPayWallet btcPayWallet,
        PullPaymentHostedService pullPaymentHostedService,
        DerivationStrategyBase derivationScheme,
        ExplorerClient explorerClient,
        BTCPayKeyChain keyChain,
        string storeId,
        WabisabiStoreSettings wabisabiStoreSettings,
        IUTXOLocker utxoLocker,
        ILoggerFactory loggerFactory,
        StoreRepository storeRepository,
        IMemoryCache memoryCache)
    {
        KeyChain = keyChain;
        _walletRepository = walletRepository;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _bitcoinLikePayoutHandler = bitcoinLikePayoutHandler;
        _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
        _btcPayWallet = btcPayWallet;
        _pullPaymentHostedService = pullPaymentHostedService;
        DerivationScheme = derivationScheme;
        ExplorerClient = explorerClient;
        StoreId = storeId;
        WabisabiStoreSettings = wabisabiStoreSettings;
        UtxoLocker = utxoLocker;
        _storeRepository = storeRepository;
        _memoryCache = memoryCache;
        Logger = loggerFactory.CreateLogger($"BTCPayWallet_{storeId}");

    }

    public string StoreId { get; set; }

    public string WalletName => StoreId;
    public bool IsUnderPlebStop => !WabisabiStoreSettings.Active;

    bool IWallet.IsMixable(string coordinator)
    {
        return KeyChain is BTCPayKeyChain {KeysAvailable: true} && WabisabiStoreSettings.Settings.SingleOrDefault(
            settings =>
                settings.Coordinator.Equals(coordinator))?.Enabled is true;
    }

    public IKeyChain KeyChain { get; }
    public IDestinationProvider DestinationProvider => this;

    public int AnonScoreTarget => WabisabiStoreSettings.PlebMode? 5:  WabisabiStoreSettings.AnonymitySetTarget;
    public bool ConsolidationMode => !WabisabiStoreSettings.PlebMode && WabisabiStoreSettings.ConsolidationMode;
    public TimeSpan FeeRateMedianTimeFrame => TimeSpan.FromHours(WabisabiStoreSettings.PlebMode?
        KeyManager.DefaultFeeRateMedianTimeFrameHours: WabisabiStoreSettings.FeeRateMedianTimeFrameHours);
    public bool RedCoinIsolation => !WabisabiStoreSettings.PlebMode &&WabisabiStoreSettings.RedCoinIsolation;
    public bool BatchPayments => WabisabiStoreSettings.PlebMode || WabisabiStoreSettings.BatchPayments;
    public long? MinimumDenominationAmount => WabisabiStoreSettings.PlebMode? 10000 : WabisabiStoreSettings.MinimumDenominationAmount;

    public async Task<bool> IsWalletPrivateAsync()
    {
        return !BatchPayments && await GetPrivacyPercentageAsync() >= 1 && (WabisabiStoreSettings.PlebMode ||
                                                                            string.IsNullOrEmpty(WabisabiStoreSettings
                                                                                .MixToOtherWallet));
    }

    public async Task<double> GetPrivacyPercentageAsync()
    {
        return GetPrivacyPercentage(await GetAllCoins(), AnonScoreTarget);
    }

    public async Task<CoinsView> GetAllCoins()
    {
        await _savingProgress;
        
        var utxos = await _btcPayWallet.GetUnspentCoins(DerivationScheme);
        var utxoLabels = await GetUtxoLabels(_memoryCache ,_walletRepository, StoreId,utxos, false);
        await _smartifier.LoadCoins(utxos.ToList(), 1, utxoLabels);
        var coins = await Task.WhenAll(_smartifier.Coins.Where(pair => utxos.Any(data => data.OutPoint == pair.Key))
            .Select(pair => pair.Value));

        
        foreach (var c in coins)
        {
            var utxo = utxos.Single(coin => coin.OutPoint == c.Outpoint);
            c.Height = utxo.Confirmations > 0 ? new Height((uint) utxo.Confirmations) : Height.Mempool;
        }
        
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
    public Smartifier _smartifier => (KeyChain as BTCPayKeyChain)?.Smartifier;
    private readonly StoreRepository _storeRepository;
    private readonly IMemoryCache _memoryCache;

    public IRoundCoinSelector GetCoinSelector()
    {
        _coinSelector??= new BTCPayCoinjoinCoinSelector(this,  Logger );
        return _coinSelector;
    }

    public bool IsRoundOk(RoundParameters roundParameters, string coordinatorName)
    {
      var coordSettings =  WabisabiStoreSettings.Settings.Find(settings => settings.Coordinator == coordinatorName && settings.Enabled);
      return coordSettings is not null && IsRoundOk(roundParameters, coordSettings);
    }

    public async Task CompletedCoinjoin(CoinJoinTracker finishedCoinJoin)
    {
        try
        {

            var successfulCoinJoinResult = (await finishedCoinJoin.CoinJoinTask) as SuccessfulCoinJoinResult;

            await RegisterCoinjoinTransaction(successfulCoinJoinResult,
                finishedCoinJoin.CoinJoinClient.CoordinatorName);
        }
        catch (Exception e)
        {
            
        }
        
        
    }

    public static bool IsRoundOk(RoundParameters roundParameters, WabisabiStoreCoordinatorSettings coordSettings)
    {
        try
        {
            return coordSettings.RoundWhenEnabled is not null &&
                   roundParameters.CoordinationFeeRate.Rate <= coordSettings.RoundWhenEnabled.CoordinationFeeRate &&
                   roundParameters.CoordinationFeeRate.PlebsDontPayThreshold <=
                   coordSettings.RoundWhenEnabled.PlebsDontPayThresholdM &&
                   roundParameters.MinInputCountByRound <= coordSettings.RoundWhenEnabled.MinInputCountByRound;
        }
        catch (Exception e)
        {
            return false;
        }
    }
    public async Task<IEnumerable<SmartCoin>> GetCoinjoinCoinCandidatesAsync(string coordinatorName)
    {
        try
        {
            
            await _savingProgress;
            if (IsUnderPlebStop)
            {
                return Array.Empty<SmartCoin>();
            }
            
            var utxos = await   _btcPayWallet.GetUnspentCoins(DerivationScheme, true, CancellationToken.None);
            var utxoLabels = await GetUtxoLabels(_memoryCache, _walletRepository, StoreId,utxos, false);
            if (!WabisabiStoreSettings.PlebMode)
            {
                if (WabisabiStoreSettings.InputLabelsAllowed?.Any() is true)
                {

                    utxos = utxos.Where(data =>
                        utxoLabels.TryGetValue(data.OutPoint, out var opLabels) &&
                        opLabels.labels.Any(
                            l => WabisabiStoreSettings.InputLabelsAllowed.Any(s => l == s))).ToArray();
                }

                if (WabisabiStoreSettings.InputLabelsExcluded?.Any() is true)
                {
                    
                    utxos = utxos.Where(data =>
                        !utxoLabels.TryGetValue(data.OutPoint, out var opLabels) ||
                        opLabels.labels.All(
                            l => WabisabiStoreSettings.InputLabelsExcluded.All(s =>l != s))).ToArray();
                }
            }

            if (WabisabiStoreSettings.PlebMode || WabisabiStoreSettings.CrossMixBetweenCoordinatorsMode != WabisabiStoreSettings.CrossMixMode.Always)
            {
                utxos = utxos.Where(data =>
                        !utxoLabels.TryGetValue(data.OutPoint, out var opLabels) ||
                        opLabels.coinjoinData is null ||
                        opLabels.coinjoinData.CoordinatorName == coordinatorName ||
                        //the next criteria is handled in our coin selector as we dnt yet have access to round parameters
                        (WabisabiStoreSettings.CrossMixBetweenCoordinatorsMode == WabisabiStoreSettings.CrossMixMode.WhenFree))
                    .ToArray();
            }

            var locks = await UtxoLocker.FindLocks(utxos.Select(data => data.OutPoint).ToArray());
            utxos = utxos.Where(data => !locks.Contains(data.OutPoint)).Where(data => data.Confirmations > 0).ToArray();
            await _smartifier.LoadCoins(utxos.Where(data => data.Confirmations>0).ToList(), 1, utxoLabels);
            
            var resultX =  await Task.WhenAll(_smartifier.Coins.Where(pair =>  utxos.Any(data => data.OutPoint == pair.Key))
                .Select(pair => pair.Value));

            foreach (SmartCoin c in resultX)
            {
                var utxo = utxos.Single(coin => coin.OutPoint == c.Outpoint);
                c.Height = utxo.Confirmations > 0 ? new Height((uint) utxo.Confirmations) : Height.Mempool;
            }
            
            return resultX;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not compute coin candidate");
            return Array.Empty<SmartCoin>();
        }
    }

    
    
    public static async Task<Dictionary<OutPoint, (HashSet<string> labels, double anonset, CoinjoinData coinjoinData)>> GetUtxoLabels(IMemoryCache memoryCache, WalletRepository walletRepository, string storeId ,ReceivedCoin[] utxos, bool isDepth)
    {
        var utxoToQuery = utxos.ToArray();
        var cacheResult = new Dictionary<OutPoint, (HashSet<string> labels, double anonset, CoinjoinData coinjoinData)>();
        foreach (var utxo in utxoToQuery)
        {
                
            if (memoryCache.TryGetValue<(HashSet<string> labels, double anonset, CoinjoinData coinjoinData)>(
                    $"wabisabi_{utxo.OutPoint}_utxo", out var cacheVariant ) )
            {
                if (!cacheResult.TryAdd(utxo.OutPoint, cacheVariant))
                {
                    //wtf!
                    
                }
            }
        }
        utxoToQuery = utxoToQuery.Where(utxo => !cacheResult.ContainsKey(utxo.OutPoint)).ToArray();
        var walletTransactionsInfoAsync = await walletRepository.GetWalletTransactionsInfo(new WalletId(storeId, "BTC"),
            utxoToQuery.SelectMany(GetWalletObjectsQuery.Get).Distinct().ToArray());

        var utxoLabels = utxoToQuery.Select(coin =>
            {
                walletTransactionsInfoAsync.TryGetValue(coin.OutPoint.Hash.ToString(), out var info1);
                walletTransactionsInfoAsync.TryGetValue(coin.Address.ToString(), out var info2);
                walletTransactionsInfoAsync.TryGetValue(coin.OutPoint.ToString(), out var info3);
                var info = walletRepository.Merge(info1, info2, info3);
                if (info is null)
                {
                    return (coin.OutPoint, null);
                }

                return (coin.OutPoint, info);
            }).Where(tuple => tuple.info is not null).DistinctBy(tuple => tuple.OutPoint)
            .ToDictionary(tuple => tuple.OutPoint, pair =>
        {
            var labels = new HashSet<string>();
            if (pair.info.LabelColors.Any())
            {
                labels.AddRange((pair.info.LabelColors.Select(pair => pair.Key)));
            }
            if (pair.info.Attachments.Any() is true)
            {
                labels.AddRange((pair.info.Attachments.Select(attachment => attachment.Id)));
            }
            var cjData = pair.info.Attachments
                .FirstOrDefault(attachment => attachment.Type == "coinjoin")?.Data
                ?.ToObject<CoinjoinData>();
            
            var explicitAnonset = pair.info.Attachments.FirstOrDefault(attachment => attachment.Type == "anonset")
                ?.Id;
            double anonset = 1;
            if (!string.IsNullOrEmpty(explicitAnonset))
            {
                anonset = double.Parse(explicitAnonset);
            }else if (cjData is not null)
            {
                var utxo = cjData.CoinsOut.FirstOrDefault(dataCoin => dataCoin.Outpoint == pair.OutPoint.ToString());
                if (utxo is not null)
                {
                    anonset = utxo.AnonymitySet;
                }
            }

            anonset = anonset < 1 ? 1 : anonset;
            return (labels, anonset, cjData);

        });
        foreach (var pair in utxoLabels)
        {
            memoryCache.Set($"wabisabi_{pair.Key.Hash}_utxo", pair.Value, isDepth? TimeSpan.FromMinutes(10): TimeSpan.FromMinutes(5));
        }
        return utxoLabels.Concat(cacheResult).ToDictionary(pair => pair.Key, pair => pair.Value);
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

    public async Task RegisterCoinjoinTransaction(SuccessfulCoinJoinResult result, string coordinatorName)
    {
        await _savingProgress;
        _savingProgress = RegisterCoinjoinTransactionInternal(result, coordinatorName);
        await _savingProgress;
    }
    private async Task RegisterCoinjoinTransactionInternal(SuccessfulCoinJoinResult result, string coordinatorName)
    {
        try
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var txHash = result.UnsignedCoinJoin.GetHash();
            var kp = await ExplorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
                WellknownMetadataKeys.AccountKeyPath);

            var storeIdForutxo = WabisabiStoreSettings.PlebMode ||
                                 string.IsNullOrEmpty(WabisabiStoreSettings.MixToOtherWallet)? StoreId: WabisabiStoreSettings.MixToOtherWallet;
            var utxoDerivationScheme = DerivationScheme;
            if (storeIdForutxo != StoreId)
            {
                var s = await _storeRepository.FindStore(storeIdForutxo);
                var scheme = s.GetDerivationSchemeSettings(_btcPayNetworkProvider, "BTC");
                utxoDerivationScheme = scheme.AccountDerivation;
            }
            List<(IndexedTxOut txout, Task<KeyPathInformation>)> scriptInfos = new();
            

            Dictionary<IndexedTxOut, PendingPayment> indexToPayment = new();
            foreach (var script in result.Outputs)
            {
                var txout = result.UnsignedCoinJoin.Outputs.AsIndexedOutputs()
                    .Single(@out => @out.TxOut.ScriptPubKey == script.ScriptPubKey && @out.TxOut.Value == script.Value);

                
                //this was not a mix to self, but rather a payment
                var isPayment = result.HandledPayments.Where(pair =>
                    pair.Key.ScriptPubKey == txout.TxOut.ScriptPubKey && pair.Key.Value == txout.TxOut.Value);
                if (isPayment.Any())
                {
                    indexToPayment.Add(txout, isPayment.First().Value);
                   continue;
                }

                var privateEnough = result.Coins.All(c => c.AnonymitySet >= WabisabiStoreSettings.AnonymitySetTarget );
                scriptInfos.Add((txout, ExplorerClient.GetKeyInformationAsync(BlockchainAnalyzer.StdDenoms.Contains(txout.TxOut.Value)&& privateEnough?utxoDerivationScheme:DerivationScheme, script.ScriptPubKey)));
            }

            await Task.WhenAll(scriptInfos.Select(t => t.Item2));
            var scriptInfos2 = scriptInfos.Where(tuple => tuple.Item2.Result is not null).ToDictionary(tuple => tuple.txout.TxOut.ScriptPubKey);
            var smartTx = new SmartTransaction(result.UnsignedCoinJoin, new Height(HeightType.Unknown));
            result.Coins.ForEach(coin =>
            {
                coin.HdPubKey.SetKeyState(KeyState.Used);
                coin.SpenderTransaction = smartTx;
                smartTx.TryAddWalletInput(SmartCoin.Clone(coin));
            });
            result.Outputs.ForEach(s =>
            {
                if (scriptInfos2.TryGetValue(s.ScriptPubKey, out var si))
                {
                    var derivation = DerivationScheme.GetChild(si.Item2.Result.KeyPath).GetExtPubKeys().First()
                        .PubKey;
                    var hdPubKey = new HdPubKey(derivation, kp.Derive(si.Item2.Result.KeyPath).KeyPath,
                        LabelsArray.Empty, 
                        KeyState.Used);
                    
                    var coin = new SmartCoin(smartTx, si.txout.N, hdPubKey);
                    smartTx.TryAddWalletOutput(coin);
                }
            });
            
            try
            {
                BlockchainAnalyzer.Analyze(smartTx);
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to analyze anonsets of tx {smartTx.GetHash()}");
            }

            
            
            var cjData = new CoinjoinData()
            {
                Round = result.RoundId.ToString(),
                CoordinatorName = coordinatorName,
                Transaction = txHash.ToString(),
                CoinsIn =   result.Coins.Select(coin => new CoinjoinData.CoinjoinDataCoin()
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
            };
            foreach (var smartTxWalletOutput in smartTx.WalletOutputs)
            {
                Smartifier.SetIsSufficientlyDistancedFromExternalKeys(smartTxWalletOutput, cjData);
            }
            
            var attachments = new List<Attachment>()
            {
                new("coinjoin", result.RoundId.ToString(), JObject.FromObject(cjData)),
                new(coordinatorName, null, null)

            };
            
            
            if (result.HandledPayments.Any())
            {
                attachments.AddRange(result.HandledPayments.Select(payment => new Attachment("payout", payment.Value.Identifier)));
            }
            await _walletRepository.AddWalletTransactionAttachment(
                new WalletId(StoreId, "BTC"),
                result.UnsignedCoinJoin.GetHash(),
                attachments);
            
            
            var mixedCoins = smartTx.WalletOutputs.Where(coin =>
                coin.AnonymitySet > 1 && BlockchainAnalyzer.StdDenoms.Contains(coin.TxOut.Value.Satoshi));
             if (storeIdForutxo != StoreId)
            {
                await _walletRepository.AddWalletTransactionAttachment(
                    new WalletId(storeIdForutxo, "BTC"),
                    txHash,
                    new List<Attachment>()
                    {
                        new Attachment("coinjoin", result.RoundId.ToString(), JObject.FromObject(new CoinjoinData()
                        {
                            Transaction =  txHash.ToString(),
                            Round = result.RoundId.ToString(),
                            CoinsOut = mixedCoins.Select(coin => new CoinjoinData.CoinjoinDataCoin()
                            {
                                AnonymitySet = coin.AnonymitySet,
                                Amount = coin.Amount.ToDecimal(MoneyUnit.BTC),
                                Outpoint = coin.Outpoint.ToString()
                            }).ToArray(),
                            CoordinatorName = coordinatorName
                        })),
                        new Attachment(coordinatorName, null, null)
                    });
            }


             
             foreach (var mixedCoin in mixedCoins)
             {
                await  _walletRepository.AddWalletTransactionAttachment(new WalletId(storeIdForutxo, "BTC"),
                     mixedCoin.Outpoint.ToString(),
                     new[] {new Attachment("anonset", mixedCoin.AnonymitySet.ToString(), JObject.FromObject(new
                     {
                         Tooltip = $"This coin has an anonset score of {mixedCoin.AnonymitySet.ToString()} (anonset-{mixedCoin.AnonymitySet.ToString()})"
                     }))}, "utxo");

             }
            _smartifier.SmartTransactions.AddOrReplace(txHash, Task.FromResult(smartTx));
            smartTx.WalletOutputs.ForEach(coin =>
            {
                
                _smartifier.Coins.AddOrReplace(coin.Outpoint, Task.FromResult(coin));
            }); 
            
            //
            // var kp = await ExplorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
            //     WellknownMetadataKeys.AccountKeyPath);
            //
            // var stopwatch = Stopwatch.StartNew();
            // Logger.LogInformation($"Registering coinjoin result for {StoreId}");
            //
            // var storeIdForutxo = WabisabiStoreSettings.PlebMode ||
            //     string.IsNullOrEmpty(WabisabiStoreSettings.MixToOtherWallet)? StoreId: WabisabiStoreSettings.MixToOtherWallet;
            // var client = await BtcPayServerClientFactory.Create(null, StoreId);
            // BTCPayServerClient utxoClient = client;
            // DerivationStrategyBase utxoDerivationScheme = DerivationScheme;
            // if (storeIdForutxo != StoreId)
            // {
            //     utxoClient = await BtcPayServerClientFactory.Create(null, storeIdForutxo);
            //     var pm  = await utxoClient.GetStoreOnChainPaymentMethod(storeIdForutxo, "BTC");
            //     utxoDerivationScheme = ExplorerClient.Network.DerivationStrategyFactory.Parse(pm.DerivationScheme);
            // }
            // var kp = await ExplorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
            //     WellknownMetadataKeys.AccountKeyPath);
            //
            // //mark the tx as a coinjoin at a specific coordinator
            // var txObject = new AddOnChainWalletObjectRequest() {Id = result.UnsignedCoinJoin.GetHash().ToString(), Type = "tx"};
            //
            // var labels = new[]
            // {
            //     new AddOnChainWalletObjectRequest() {Id = "coinjoin", Type = "label"},
            //     new AddOnChainWalletObjectRequest() {Id = coordinatorName, Type = "label"}
            // };
            //

            //
            // await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", txObject);
            // if(storeIdForutxo != StoreId)
            //     await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", txObject);
            
            // foreach (var label in labels)
            // {
            //     await client.AddOrUpdateOnChainWalletObject(StoreId, "BTC", label);
            //     await client.AddOrUpdateOnChainWalletLink(StoreId, "BTC", txObject, new AddOnChainWalletObjectLinkRequest()
            //     {
            //         Id = label.Id,
            //         Type = label.Type
            //     }, CancellationToken.None);
            //
            //     if (storeIdForutxo != StoreId)
            //     {await utxoClient.AddOrUpdateOnChainWalletObject(storeIdForutxo, "BTC", label);
            //         await utxoClient.AddOrUpdateOnChainWalletLink(storeIdForutxo, "BTC", txObject, new AddOnChainWalletObjectLinkRequest()
            //         {
            //             Id = label.Id,
            //             Type = label.Type
            //         }, CancellationToken.None);
            //     }
            // }


                stopwatch.Stop();
                
                Logger.LogInformation($"Registered coinjoin result for {StoreId} in {stopwatch.Elapsed}");
                _memoryCache.Remove(WabisabiService.GetCacheKey(StoreId) + "cjhistory");

        }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not save coinjoin progress!");
            // ignored
        }
    }


    // public async Task UnlockUTXOs()
    // {
    //     var client = await BtcPayServerClientFactory.Create(null, StoreId);
    //     var utxos = await client.GetOnChainWalletUTXOs(StoreId, "BTC");
    //     var unlocked = new List<string>();
    //     foreach (OnChainWalletUTXOData utxo in utxos)
    //     {
    //
    //         if (await UtxoLocker.TryUnlock(utxo.Outpoint))
    //         {
    //             unlocked.Add(utxo.Outpoint.ToString());
    //         }
    //     }
    //
    //     Logger.LogTrace($"unlocked utxos: {string.Join(',', unlocked)}");
    // }

public async Task<IEnumerable<IDestination>> GetNextDestinationsAsync(int count, bool mixedOutputs, bool privateEnough)
    {
        if (!WabisabiStoreSettings.PlebMode && !string.IsNullOrEmpty(WabisabiStoreSettings.MixToOtherWallet) && mixedOutputs && privateEnough)
        {
            try
            {
                var mixStore = await  _storeRepository.FindStore(WabisabiStoreSettings.MixToOtherWallet);
                var pm =  mixStore.GetDerivationSchemeSettings(_btcPayNetworkProvider, "BTC");
                
                
               if (pm?.AccountDerivation?.ScriptPubKeyType() == DerivationScheme.ScriptPubKeyType())
               {
                   return  await  Task.WhenAll(Enumerable.Repeat(0, count).Select(_ =>
                       _btcPayWallet.ReserveAddressAsync(WabisabiStoreSettings.MixToOtherWallet, pm.AccountDerivation, "coinjoin"))).ContinueWith(task => task.Result.Select(information => information.Address));
               }
            }
            
            catch (Exception e)
            {
                WabisabiStoreSettings.MixToOtherWallet = null;
            }
        }
        return  await  Task.WhenAll(Enumerable.Repeat(0, count).Select(_ =>
            _btcPayWallet.ReserveAddressAsync(StoreId ,DerivationScheme, "coinjoin"))).ContinueWith(task => task.Result.Select(information => information.Address));
    }

    public async Task<IEnumerable<PendingPayment>> GetPendingPaymentsAsync( UtxoSelectionParameters roundParameters)
    {
        
        
        try
        {
           var payouts = (await _pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
           {
               States = new [] {PayoutState.AwaitingPayment},
               Stores = new []{StoreId},
               PaymentMethods = new []{"BTC"}
           })).Select(async data =>
           {
               
               var  claim = await _bitcoinLikePayoutHandler.ParseClaimDestination(new PaymentMethodId("BTC", BitcoinPaymentType.Instance),
                   data.Destination, CancellationToken.None);

               if (!string.IsNullOrEmpty(claim.error) || claim.destination is not IBitcoinLikeClaimDestination bitcoinLikeClaimDestination )
               {
                   return null;
               }

               var payoutBlob = data.GetBlob(_btcPayNetworkJsonSerializerSettings);
               var value = new Money(payoutBlob.CryptoAmount.Value, MoneyUnit.BTC);
               if (!roundParameters.AllowedOutputAmounts.Contains(value) ||
                   !roundParameters.AllowedOutputScriptTypes.Contains(bitcoinLikeClaimDestination.Address.ScriptPubKey.GetScriptType()))
               {
                   return null;
               }
               return new PendingPayment()
               {
                   Identifier = data.Id,
                   Destination = bitcoinLikeClaimDestination.Address,
                   Value =value,
                   PaymentStarted = PaymentStarted(data.Id),
                   PaymentFailed = PaymentFailed(data.Id),
                   PaymentSucceeded = PaymentSucceeded(data.Id),
               };
           }).ToArray();
           return (await Task.WhenAll(payouts)).Where(payment => payment is not null).ToArray();
        }
        catch (Exception e)
        {
            return Array.Empty<PendingPayment>();
        }
    }

    public Task<ScriptType> GetScriptTypeAsync()
    {
        return Task.FromResult(DerivationScheme.GetDerivation(0).ScriptPubKey.GetScriptType());
    }

    private Action<(uint256 roundId, uint256 transactionId, int outputIndex)> PaymentSucceeded(string payoutId)
    {
        
        return tuple =>
            _pullPaymentHostedService.MarkPaid( new HostedServices.MarkPayoutRequest()
            {
                PayoutId = payoutId,
                State = PayoutState.InProgress,
                Proof = JObject.FromObject(new PayoutTransactionOnChainBlob()
                {
                    Candidates = new HashSet<uint256>()
                    {
                        tuple.transactionId
                    },
                    TransactionId = tuple.transactionId
                })
            });
    }

    private Action PaymentFailed(string payoutId)
    {
        return () =>
        {
            _pullPaymentHostedService.MarkPaid(new HostedServices.MarkPayoutRequest()
            {
                PayoutId = payoutId,
                State = PayoutState.AwaitingPayment
            });
        };
    }

    private Func<Task<bool>> PaymentStarted(string payoutId)
    {
        return async () =>
        {
            try
            {
                await _pullPaymentHostedService.MarkPaid( new HostedServices.MarkPayoutRequest()
                {
                    PayoutId = payoutId,
                    State = PayoutState.InProgress,
                    Proof = JObject.FromObject(new WabisabiPaymentProof())
                });
                return true;
            }
            catch (Exception e)
            {
                return false;
            }
        };
    }

    public class WabisabiPaymentProof
    {
        [JsonProperty("proofType")]
        public string ProofType { get; set; } = "Wabisabi";
        [JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
        public uint256 TransactionId { get; set; }
        [JsonProperty(ItemConverterType = typeof(NBitcoin.JsonConverters.UInt256JsonConverter), NullValueHandling = NullValueHandling.Ignore)]
        public HashSet<uint256> Candidates { get; set; } = new HashSet<uint256>();
        public string Link { get; set; }
    }
}
