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
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;

namespace BTCPayServer.Plugins.Wabisabi;

public class Smartifier
{
    private readonly WalletRepository _walletRepository;
    private readonly ExplorerClient _explorerClient;
    public DerivationStrategyBase DerivationScheme { get; }
    private readonly string _storeId;
    private readonly IUTXOLocker _utxoLocker;

    public Smartifier(
        WalletRepository walletRepository,
        ExplorerClient explorerClient, DerivationStrategyBase derivationStrategyBase, string storeId,
        IUTXOLocker utxoLocker)
    {
        _walletRepository = walletRepository;
        _explorerClient = explorerClient;
        DerivationScheme = derivationStrategyBase;
        _storeId = storeId;
        _utxoLocker = utxoLocker;
        _accountKeyPath = _explorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
            WellknownMetadataKeys.AccountKeyPath);
    }

    public readonly  ConcurrentDictionary<uint256, Task<TransactionInformation>> CachedTransactions = new();
    public readonly ConcurrentDictionary<uint256, Task<SmartTransaction>> Transactions = new();
    public readonly  ConcurrentDictionary<OutPoint, Task<SmartCoin>> Coins = new();
    private readonly Task<RootedKeyPath> _accountKeyPath;

    public async Task LoadCoins(List<ReceivedCoin> coins, int current ,
        Dictionary<OutPoint, (HashSet<string> labels, double anonset, BTCPayWallet.CoinjoinData coinjoinData)> utxoLabels)
    {
        coins = coins.Where(data => data is not null).ToList();
        if (current > 3)
        {
            return;
        }
        var txs = coins.Select(data => data.OutPoint.Hash).Distinct();
        foreach (uint256 tx in txs)
        {
            if(!CachedTransactions.ContainsKey(tx))
                CachedTransactions.TryAdd(tx, _explorerClient.GetTransactionAsync(DerivationScheme, tx));
        }

        foreach (var coin in coins)
        {
            var tx = await Transactions.GetOrAdd(coin.OutPoint.Hash, async uint256 =>
            {
                var unsmartTx = await CachedTransactions[coin.OutPoint.Hash];
                if (unsmartTx is null)
                {
                    return null;
                }
                var smartTx = new SmartTransaction(unsmartTx.Transaction,
                    unsmartTx.Height is null ? Height.Mempool : new Height((uint)unsmartTx.Height.Value),
                    unsmartTx.BlockHash, firstSeen: unsmartTx.Timestamp);


                var ourSpentUtxos = new Dictionary<MatchedOutput, IndexedTxIn>();
                var potentialMatches = new Dictionary<MatchedOutput, IndexedTxIn[]>();
                foreach (MatchedOutput matchedInput in unsmartTx.Inputs)
                {
                    var potentialMatchesForInput = unsmartTx.Transaction.Inputs
                        .AsIndexedInputs()
                        .Where(txIn => txIn.PrevOut.N == matchedInput.Index);
                    potentialMatches.TryAdd(matchedInput, potentialMatchesForInput.ToArray());
                    foreach (IndexedTxIn potentialMatchForInput in potentialMatchesForInput)
                    {
                        var ti = await CachedTransactions.GetOrAdd(potentialMatchForInput.PrevOut.Hash,
                            _explorerClient.GetTransactionAsync(DerivationScheme,
                                potentialMatchForInput.PrevOut.Hash));

                        if (ti is not null)
                        {
                            MatchedOutput found = ti.Outputs.Find(output =>
                                matchedInput.Index == output.Index &&
                                matchedInput.Value == output.Value &&
                                matchedInput.KeyPath == output.KeyPath &&
                                matchedInput.ScriptPubKey == output.ScriptPubKey
                            );
                            if (found is not null)
                            {
                                ourSpentUtxos.Add(matchedInput, potentialMatchForInput);
                                break;
                            }
                        }
                    }
                }
                var inputsToLoad = unsmartTx.Inputs.Select(output =>
                {
                    if (!ourSpentUtxos.TryGetValue(output, out var outputtxin))
                    {
                        return null;
                    }

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
                
                await LoadCoins(inputsToLoad,current+1,  await BTCPayWallet.GetUtxoLabels(_walletRepository, _storeId, inputsToLoad.ToArray()));
                foreach (MatchedOutput input in unsmartTx.Inputs)
                {
                    if (!ourSpentUtxos.TryGetValue(input, out var outputtxin))
                    {
                        continue;
                    }
                    if (Coins.TryGetValue(outputtxin.PrevOut, out var coinTask))
                    {
                        var c = await coinTask;
                        c.SpenderTransaction = smartTx;
                        smartTx.TryAddWalletInput(c);
                        
                    }
                }
                return smartTx;
            });

            var smartCoin = await Coins.GetOrAdd(coin.OutPoint, async point =>
            {
                utxoLabels.TryGetValue(coin.OutPoint, out var labels);
                var unsmartTx = await CachedTransactions[coin.OutPoint.Hash];
                var pubKey = DerivationScheme.GetChild(coin.KeyPath).GetExtPubKeys().First().PubKey;
                var kp = (await _accountKeyPath).Derive(coin.KeyPath).KeyPath;

                var hdPubKey = new HdPubKey(pubKey, kp, new SmartLabel(labels.labels ?? new HashSet<string>()),
                    current == 1 ? KeyState.Clean : KeyState.Used);

                hdPubKey.SetAnonymitySet(labels.anonset);
                var c = new SmartCoin(tx, coin.OutPoint.N, hdPubKey);
                c.PropertyChanged += CoinPropertyChanged;
                return c;
            });
            
            utxoLabels.TryGetValue(coin.OutPoint, out var labels);
            smartCoin.HdPubKey.SetLabel(new SmartLabel(labels.labels ?? new HashSet<string>()));
            smartCoin.HdPubKey.SetKeyState(current == 1 ? KeyState.Clean : KeyState.Used);
            smartCoin.HdPubKey.SetAnonymitySet(labels.anonset);
            tx.TryAddWalletOutput(smartCoin);
            
        }
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