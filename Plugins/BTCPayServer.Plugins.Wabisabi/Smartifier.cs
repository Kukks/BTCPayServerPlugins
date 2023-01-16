using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
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
    private readonly ExplorerClient _explorerClient;
    public DerivationStrategyBase DerivationScheme { get; }
    private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
    private readonly string _storeId;
    private readonly Action<object, PropertyChangedEventArgs> _coinOnPropertyChanged;

    public Smartifier(ExplorerClient explorerClient, DerivationStrategyBase derivationStrategyBase,
        IBTCPayServerClientFactory btcPayServerClientFactory, string storeId, 
        Action<object, PropertyChangedEventArgs> coinOnPropertyChanged)
    {
        _explorerClient = explorerClient;
        DerivationScheme = derivationStrategyBase;
        _btcPayServerClientFactory = btcPayServerClientFactory;
        _storeId = storeId;
        _coinOnPropertyChanged = coinOnPropertyChanged;
        _accountKeyPath = _explorerClient.GetMetadataAsync<RootedKeyPath>(DerivationScheme,
            WellknownMetadataKeys.AccountKeyPath);
        
    }

    private ConcurrentDictionary<uint256, Task<TransactionInformation>> cached = new();
    public readonly ConcurrentDictionary<uint256, Task<SmartTransaction>> Transactions = new();
    public readonly  ConcurrentDictionary<OutPoint, Task<SmartCoin>> Coins = new();
    private readonly Task<RootedKeyPath> _accountKeyPath;

    public async Task LoadCoins(List<OnChainWalletUTXOData> coins, int current = 1)
    {
        coins = coins.Where(data => data is not null).ToList();
        if (current > 3)
        {
            return;
        }
        var txs = coins.Select(data => data.Outpoint.Hash).Distinct();
        foreach (uint256 tx in txs)
        {
            cached.TryAdd(tx, _explorerClient.GetTransactionAsync(DerivationScheme, tx));
        }

        foreach (OnChainWalletUTXOData coin in coins)
        {
            var client = await _btcPayServerClientFactory.Create(null, _storeId);
            var tx = await Transactions.GetOrAdd(coin.Outpoint.Hash, async uint256 =>
            {
                var unsmartTx = await cached[coin.Outpoint.Hash];
                if (unsmartTx is null)
                {
                    return null;
                }
                var smartTx = new SmartTransaction(unsmartTx.Transaction,
                    unsmartTx.Height is null ? Height.Mempool : new Height((uint)unsmartTx.Height.Value),
                    unsmartTx.BlockHash, firstSeen: unsmartTx.Timestamp);
                //var indexesOfOurSpentInputs = unsmartTx.Inputs.Select(output => (uint)output.Inputndex).ToArray();
                // var ourSpentUtxos = unsmartTx.Transaction.Inputs.AsIndexedInputs()
                //     .Where(@in => indexesOfOurSpentInputs.Contains(@in.Index)).ToDictionary(@in=> @in.Index,@in => @in);


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
                        var ti = await cached.GetOrAdd(potentialMatchForInput.PrevOut.Hash,
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
                var utxoObjects = await client.GetOnChainWalletObjects(_storeId, "BTC",
                    new GetWalletObjectsRequest()
                    {
                        Ids = ourSpentUtxos.Select(point => point.Value.PrevOut.ToString()).ToArray(),
                        Type = "utxo",
                        IncludeNeighbourData = true
                    });
                var labelsOfOurSpentUtxos =utxoObjects.ToDictionary(data => data.Id,
                    data => data.Links.Where(link => link.Type == "label"));
                
                
                await LoadCoins(unsmartTx.Inputs.Select(output =>
                {
                    if (!ourSpentUtxos.TryGetValue(output, out var outputtxin))
                    {
                        return null;
                    }
                    var outpoint = outputtxin.PrevOut;
                    var labels = labelsOfOurSpentUtxos
                        .GetValueOrDefault(outpoint.ToString(),
                            new List<OnChainWalletObjectData.OnChainWalletObjectLink>())
                        .ToDictionary(link => link.Id, link => new LabelData());
                    return new OnChainWalletUTXOData()
                    {
                        Timestamp = DateTimeOffset.Now,
                        Address =
                            output.Address?.ToString() ?? _explorerClient.Network
                                .CreateAddress(DerivationScheme, output.KeyPath, output.ScriptPubKey)
                                .ToString(),
                        KeyPath = output.KeyPath,
                        Amount = ((Money)output.Value).ToDecimal(MoneyUnit.BTC),
                        Outpoint = outpoint,
                        Labels = labels,
                        Confirmations = unsmartTx.Confirmations
                    };
                }).ToList(),current+1);
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

            var smartCoin = await Coins.GetOrAdd(coin.Outpoint, async point =>
            {

                var unsmartTx = await cached[coin.Outpoint.Hash];
                var pubKey = DerivationScheme.GetChild(coin.KeyPath).GetExtPubKeys().First().PubKey;
                var kp = (await _accountKeyPath).Derive(coin.KeyPath).KeyPath;
                var hdPubKey = new HdPubKey(pubKey, kp, new SmartLabel(coin.Labels.Keys.ToList()),
                    current == 1 ? KeyState.Clean : KeyState.Used);
                var anonsetLabel = coin.Labels.Keys.FirstOrDefault(s => s.StartsWith("anonset-"))
                    ?.Split("-", StringSplitOptions.RemoveEmptyEntries)?.ElementAtOrDefault(1) ?? "1";
                hdPubKey.SetAnonymitySet(double.Parse(anonsetLabel));

                var c =  new SmartCoin(tx, coin.Outpoint.N, hdPubKey);
                c.PropertyChanged += CoinPropertyChanged;
                return c;
            });
            tx.TryAddWalletOutput(smartCoin);
            
        }
    }

    private void CoinPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        _coinOnPropertyChanged.Invoke(sender, e);
    }
}
