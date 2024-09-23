using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;

namespace BTCPayServer.Plugins.Wabisabi;

public class Smartifier
{
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger _logger;
    private readonly WalletRepository _walletRepository;
    private readonly ExplorerClient _explorerClient;
    public DerivationStrategyBase DerivationScheme { get; }
    private readonly string _storeId;
    private readonly IUTXOLocker _utxoLocker;

    public Smartifier(
        IMemoryCache  memoryCache,
        ILogger logger, 
        WalletRepository walletRepository,
        ExplorerClient explorerClient, DerivationStrategyBase derivationStrategyBase, string storeId,
        IUTXOLocker utxoLocker, RootedKeyPath accountKeyPath)
    {
        _memoryCache = memoryCache;
        _logger = logger;
        _walletRepository = walletRepository;
        _explorerClient = explorerClient;
        DerivationScheme = derivationStrategyBase;
        _storeId = storeId;
        _utxoLocker = utxoLocker;
        _accountKeyPath = accountKeyPath;

        _ = LoadInitialTxs();
    }
    
    private async Task LoadInitialTxs()
    {
        try
        {
            var txsBulk = await _explorerClient.GetTransactionsAsync(DerivationScheme);
            foreach (var transactionInformation in txsBulk.ConfirmedTransactions.Transactions.Concat(txsBulk.UnconfirmedTransactions.Transactions))
            {
                TransactionInformations.AddOrReplace(transactionInformation.TransactionId,
                    new Lazy<Task<TransactionInformation>>(() => Task.FromResult(transactionInformation)));
            }
        }
        finally
        {
            
            _loadInitialTxs.TrySetResult();
        }
        

    }
    
    private TaskCompletionSource _loadInitialTxs = new();
    
     public readonly  ConcurrentDictionary<uint256, Lazy<Task<TransactionInformation>>> TransactionInformations = new();
     public readonly ConcurrentDictionary<uint256, Task<SmartTransaction>> SmartTransactions = new();
    public readonly  ConcurrentDictionary<OutPoint, Task<SmartCoin>> Coins = new();

    public static async Task<T?> GetOrCreate<T, Y>(ConcurrentDictionary<Y, Lazy<Task<T?>>> collection, Y key, Func<Task<T?>> create, ILogger logger = null)
    {
        
        try
        {
            var lazyTask = new Lazy<Task<T?>>(() => FetchFromServer(create, logger, key));

            // Even if multiple threads provide their own new Lazy instances, only one will be stored.
            var task = collection.GetOrAdd(key, lazyTask).Value;

            return await task;
        }
        catch (Exception)
        {
            // If there's an error, remove the lazy task from the dictionary.
            collection.TryRemove(key, out _);
            // The error has already been logged inside FetchFromServer.
            return default;
        }
    }

    private static async Task<T?> FetchFromServer<T, Y>(Func<Task<T?>> create, ILogger logger, Y key)
    {
        try
        {
            return await create();
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Error while loading(and caching) {key}", key);
            throw; // Re-throw the exception so the outer catch can handle it.
        }
    }

    
    public async Task<TransactionInformation?> GetTransactionInfo(uint256 hash)
    {
        
        return await GetOrCreate(TransactionInformations , hash, () => _explorerClient.GetTransactionAsync(DerivationScheme, hash), _logger);
    }
    
    private readonly RootedKeyPath _accountKeyPath;

    public async Task LoadCoins(List<ReceivedCoin> coins, int current ,
        Dictionary<OutPoint, (HashSet<string> labels, double anonset, BTCPayWallet.CoinjoinData coinjoinData)> utxoLabels)
    {
        
        await _loadInitialTxs.Task;
        coins = coins.Where(data => data is not null).ToList();
        if (current > 3)
        {
            return;
        }
        var txs = coins.Select(data => data.OutPoint.Hash).Distinct();
        foreach (uint256 tx in txs)
        {
            _ =GetTransactionInfo(tx);
        }

        foreach (var coin in coins)
        {
            if(coin?.KeyPath is null || coin.OutPoint is null ){
                continue;
            }
            var tx = await SmartTransactions.GetOrAdd(coin.OutPoint.Hash, async uint256 =>
            {
                var unsmartTx = await GetTransactionInfo(coin.OutPoint.Hash);
                if (unsmartTx?.Transaction is null)
                {
                    return null;
                }
                var smartTx = new SmartTransaction(unsmartTx.Transaction,
                    unsmartTx.Height is null ? Height.Mempool : new Height((uint)unsmartTx.Height.Value),
                    unsmartTx.BlockHash, firstSeen: unsmartTx.Timestamp);

                var inputsToLoad = unsmartTx.Inputs.Select(output =>
                {
                     var outputtxin = unsmartTx.Transaction.Inputs
                        .AsIndexedInputs().First(@in => @in.Index == output.InputIndex);

                    var outpoint = outputtxin.PrevOut;
                    return new ReceivedCoin()
                    {
                        Timestamp = DateTimeOffset.Now,
                        Address =
                            output.Address ?? _explorerClient.Network
                                .CreateAddress(DerivationScheme, output.KeyPath, output.ScriptPubKey),
                        KeyPath = output.KeyPath,
                        Value = output.Value,
                        OutPoint = outpoint,
                        Confirmations = unsmartTx.Confirmations
                    };
                }).Where(receivedCoin => receivedCoin is not null).ToList();
                
                await LoadCoins(inputsToLoad,current+1,  await BTCPayWallet.GetUtxoLabels( _memoryCache ,_walletRepository, _storeId, inputsToLoad.ToArray(), true ));
                foreach (var input in unsmartTx.Inputs)
                {
                    var outputtxin = unsmartTx.Transaction.Inputs
                        .AsIndexedInputs().First(@in => @in.Index == input.InputIndex);
                    if (Coins.TryGetValue(outputtxin.PrevOut, out var coinTask))
                    {
                        var c = await coinTask;
                        c.SpenderTransaction = smartTx;
                        smartTx.TryAddWalletInput(c);
                    }
                }
                return smartTx;
            });

            if(tx is null){
                continue;
            }
            var smartCoin = await Coins.GetOrAdd(coin.OutPoint, async point =>
            {
                utxoLabels.TryGetValue(coin.OutPoint, out var labels);
                PubKey pubKey;
                try
                {
                    pubKey = DerivationScheme.GetChild(coin.KeyPath).GetExtPubKeys().First().PubKey;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"REPORT THIS CRASH! Derivnull? {DerivationScheme is null}, coinpath?{coin.KeyPath is null} ");
                   
                    throw;
                }
                //if there is no account key path, it most likely means this is a watch only wallet. Fake the key path
                var kp = _accountKeyPath?.Derive(coin.KeyPath).KeyPath ?? new KeyPath(0,0,0,0,0);

                var hdPubKey = new HdPubKey(pubKey, kp, new LabelsArray(labels.labels ?? new HashSet<string>()),
                    current == 1 ? KeyState.Clean : KeyState.Used);

                hdPubKey.SetAnonymitySet(labels.anonset);
                var c = new SmartCoin(tx, coin.OutPoint.N, hdPubKey);
                if (labels.coinjoinData is not null)
                {
                    
                    SetIsSufficientlyDistancedFromExternalKeys(c, labels.coinjoinData);
                }
                c.PropertyChanged += CoinPropertyChanged;
                return c;
            });
            utxoLabels.TryGetValue(coin.OutPoint, out var labels);
            smartCoin.HdPubKey.SetLabel(new LabelsArray(labels.labels ?? new HashSet<string>()));
            smartCoin.HdPubKey.SetKeyState(current == 1 ? KeyState.Clean : KeyState.Used);
            smartCoin.HdPubKey.SetAnonymitySet(labels.anonset);
            if (labels.coinjoinData is not null)
            {
                SetIsSufficientlyDistancedFromExternalKeys(smartCoin, labels.coinjoinData);
            }
            tx.TryAddWalletOutput(smartCoin);
            
        }
    }

    public static void SetIsSufficientlyDistancedFromExternalKeys(SmartCoin c, BTCPayWallet.CoinjoinData coinjoinData)
    {
        c.IsSufficientlyDistancedFromExternalKeys = coinjoinData.CoinsIn.All(dataCoin => dataCoin.AnonymitySet >1);
    }
    
    private void CoinPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (sender is SmartCoin smartCoin && e.PropertyName == nameof(SmartCoin.CoinJoinInProgress))
        {
            if (_utxoLocker is not null)
            {
                _ = (smartCoin.CoinJoinInProgress
                    ? _utxoLocker.TryLock(smartCoin.Outpoint)
                    : _utxoLocker.TryUnlock(smartCoin.Outpoint));
            }
        }
    }

}