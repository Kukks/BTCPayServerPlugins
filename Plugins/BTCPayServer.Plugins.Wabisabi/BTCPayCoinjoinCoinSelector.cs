using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Exceptions;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;

public class BTCPayCoinjoinCoinSelector : IRoundCoinSelector
{
    private readonly BTCPayWallet _wallet;

    private static BlockchainAnalyzer BlockchainAnalyzer { get; } = new();
    public BTCPayCoinjoinCoinSelector(BTCPayWallet wallet)
    {
        _wallet = wallet;
    }

    public async
        Task<(ImmutableList<SmartCoin> selected, Func<IEnumerable<AliceClient>, Task<bool>> acceptableRegistered, Func<
            ImmutableArray<AliceClient>, (IEnumerable<TxOut> outputTxOuts, Dictionary<TxOut, PendingPayment>
            batchedPayments), TransactionWithPrecomputedData, RoundState, Task<bool>> acceptableOutputs)>
        SelectCoinsAsync((IEnumerable<SmartCoin> Candidates, IEnumerable<SmartCoin> Ineligible) coinCandidates,
            RoundParameters roundParameters, Money liquidityClue, SecureRandom secureRandom, string coordinatorName)
    {
        var log = new StringBuilder();
        try
        {
SmartCoin[] FilterCoinsMore(IEnumerable<SmartCoin> coins)
        {
            return coins
                .Where(coin => roundParameters.AllowedInputTypes.Contains(coin.ScriptType))
                .Where(coin => roundParameters.AllowedInputAmounts.Contains(coin.Amount))
                .Where(coin =>
                {
                    var effV = coin.EffectiveValue(roundParameters.MiningFeeRate);
                    var percentageLeft = (effV.ToDecimal(MoneyUnit.BTC) / coin.Amount.ToDecimal(MoneyUnit.BTC));
                    // filter out low value coins where 50% of the value would be eaten up by fees
                    return effV > Money.Zero && percentageLeft >= 0.5m;
                })
                .Where(coin =>
                {
                    if (!_wallet.WabisabiStoreSettings.PlebMode &&
                        _wallet.WabisabiStoreSettings.CrossMixBetweenCoordinatorsMode ==
                        WabisabiStoreSettings.CrossMixMode.Always)
                    {
                        return true;
                    }
                    if (!coin.HdPubKey.Labels.Contains("coinjoin") || coin.HdPubKey.Labels.Contains(coordinatorName))
                    {
                        return true;
                    }


                    return false;
                    
                }).ToArray();
        }

        var candidates =
            FilterCoinsMore(coinCandidates.Candidates);
        var ineligibleCoins =
            FilterCoinsMore(coinCandidates.Ineligible);
        
        var payments =
            (_wallet.BatchPayments
                ? await _wallet.DestinationProvider.GetPendingPaymentsAsync(roundParameters)
                : Array.Empty<PendingPayment>()).ToArray();
        
        var maxPerType = new Dictionary<AnonsetType, int>();

        var attemptingTobeParanoidWhenDoingPayments = payments.Any() && _wallet.WabisabiStoreSettings.ParanoidPayments;
        var attemptingToMixToOtherWallet = !string.IsNullOrEmpty(_wallet.WabisabiStoreSettings.MixToOtherWallet);
        selectCoins:
        maxPerType.Clear();
        if (attemptingTobeParanoidWhenDoingPayments || attemptingToMixToOtherWallet)
        {
            maxPerType.Add(AnonsetType.Red,0);
            maxPerType.Add(AnonsetType.Orange,0);
        }
        
        if (_wallet.RedCoinIsolation)
        {
            maxPerType.TryAdd(AnonsetType.Red, 1);
        }

        var isLowFee = roundParameters.MiningFeeRate.SatoshiPerByte <= _wallet.LowFeeTarget;
        var consolidationMode = _wallet.ConsolidationMode switch
        {
            ConsolidationModeType.Always => true,
            ConsolidationModeType.Never => false,
            ConsolidationModeType.WhenLowFee => isLowFee,
            ConsolidationModeType.WhenLowFeeAndManyUTXO => isLowFee && candidates.Count() > BTCPayWallet.HighAmountOfCoins,
            _ => throw new ArgumentOutOfRangeException()
        };
        var mixReasons = await _wallet.ShouldMix(coordinatorName, isLowFee, payments.Any());
        if (!mixReasons.Any())
        {
            throw new CoinJoinClientException(CoinjoinError.NoCoinsEligibleToMix, "ShouldMix returned false, so we will not mix");
        }
        else
        {
            log.AppendLine($"ShouldMix returned true for {mixReasons.Length} reasons: {string.Join(", ", mixReasons)}");
        }
        Dictionary<AnonsetType, int> idealMinimumPerType = new Dictionary<AnonsetType, int>()
            {{AnonsetType.Red, 1}, {AnonsetType.Orange, 1}, {AnonsetType.Green, 1}};

        var solution = await SelectCoinsInternal(log,coordinatorName,roundParameters, candidates,payments,
            Random.Shared.Next(20, 31),
            maxPerType,
            idealMinimumPerType,
            consolidationMode, liquidityClue, secureRandom);

        if (attemptingTobeParanoidWhenDoingPayments && !solution.HandledPayments.Any())
        {
            attemptingTobeParanoidWhenDoingPayments = false;
            payments = Array.Empty<PendingPayment>();
            goto selectCoins;
        }
        
        var onlyForPayments = mixReasons.Length == 1 && mixReasons.Contains(IWallet.MixingReason.Payment);
        if (onlyForPayments && !solution.HandledPayments.Any())
        {
            throw new CoinJoinClientException(CoinjoinError.NoCoinsEligibleToMix, "ShouldMix returned true only for payments, but no handled payments were found, so we will not mix");
        }
        var onlyForConsolidation = mixReasons.Length == 1 && mixReasons.Contains(IWallet.MixingReason.Consolidation);
        if(onlyForConsolidation && solution.Coins.Count() < 10)
        {
            throw new CoinJoinClientException(CoinjoinError.NoCoinsEligibleToMix, "ShouldMix returned true only for consolidation, but less than 10 coins were found, so we will not mix");
        }
        
        log.AppendLine(solution.ToString());

        async Task<bool> AcceptableRegistered(IEnumerable<AliceClient> coins)
        {
            var remainingMixReasons = mixReasons.ToList();
            // var onlyForConsolidation = mixReasons.Length == 1 && mixReasons.Contains(IWallet.MixingReason.Consolidation);
            if (mixReasons.Contains(IWallet.MixingReason.Consolidation) && coins.Count() < 10)
            {
                remainingMixReasons.Remove(IWallet.MixingReason.Consolidation);
                //
                // _wallet.LogTrace("ShouldMix returned true only for consolidation, but less than 10 coins were registered successfully, so we will not mix");
                // return false;
            }
            
            if (mixReasons.Contains(IWallet.MixingReason.Payment) && !solution.HandledPayments.Any())
            {
                if (!solution.HandledPayments.Any()) remainingMixReasons.Remove(IWallet.MixingReason.Payment);
                // can the registered coins handle any of the solution payments?
                // if not, then we should not proceed
                // _wallet.LogTrace("ShouldMix returned true only for payments, but no handled payments were found, so we will not mix");
                //check if we can handle any of the payments in solution.HandledPayments with coins
                
                var effectiveSumOfRegisteredCoins = coins.Sum(coin => coin.EffectiveValue);
                var canHandleAPayment = solution.HandledPayments.Any(payment =>
                {
                    var cost = payment.ToTxOut().EffectiveCost(roundParameters.MiningFeeRate) + payment.Value;
                    return effectiveSumOfRegisteredCoins >= cost;
                });
                if (!canHandleAPayment)
                {
                    remainingMixReasons.Remove(IWallet.MixingReason.Payment);
                }
            }

            if (mixReasons.Contains(IWallet.MixingReason.NotPrivate) && coins.All(coin => coin.SmartCoin.IsPrivate(_wallet)))
            {
                remainingMixReasons.Remove(IWallet.MixingReason.NotPrivate);
            }

            if (coins.Count() != solution.Coins.Count() && mixReasons.Contains(IWallet.MixingReason.ExtraJoin))
            {
                remainingMixReasons.Remove(IWallet.MixingReason.ExtraJoin);
            }

            if (remainingMixReasons.Count != mixReasons.Length)
            {
                log.AppendLine($"Some mix reasons were removed due to the difference in registered coins: {string.Join(", ", mixReasons.Except(remainingMixReasons))}. Remaining: {string.Join(", ", remainingMixReasons)}");
            }

            return remainingMixReasons.Any();
        }

        async Task<bool> AcceptableOutputs(ImmutableArray<AliceClient> registeredAliceClients, (IEnumerable<TxOut> outputTxOuts, Dictionary<TxOut, PendingPayment> batchedPayments) outputTxOuts, TransactionWithPrecomputedData unsignedCoinJoin, RoundState roundState)
        {
            var remainingMixReasons = mixReasons.ToList();
            var ourCoins = registeredAliceClients.Select(client => client.SmartCoin);
            var ourOutputsThatAreNotPayments = outputTxOuts.outputTxOuts.ToList();
            foreach (var batchedPayment in outputTxOuts.batchedPayments)
            {
                ourOutputsThatAreNotPayments.Remove(ourOutputsThatAreNotPayments.First(@out => @out.ScriptPubKey == batchedPayment.Key.ScriptPubKey && @out.Value == batchedPayment.Key.Value));
            }

            var smartTx = new SmartTransaction(unsignedCoinJoin.Transaction, new Height(HeightType.Unknown));
            foreach (var smartCoin in ourCoins)
            {
                smartTx.TryAddWalletInput(SmartCoin.Clone(smartCoin));
            }

            var outputCoins = new List<SmartCoin>();
            var matchedIndexes = new List<uint>();
            foreach (var txOut in ourOutputsThatAreNotPayments)
            {
                var index = unsignedCoinJoin.Transaction.Outputs.AsIndexedOutputs().First(@out => !matchedIndexes.Contains(@out.N) && @out.TxOut.ScriptPubKey == txOut.ScriptPubKey && @out.TxOut.Value == txOut.Value).N;
                matchedIndexes.Add(index);
                var coin = new SmartCoin(smartTx, index, new HdPubKey(new Key().PubKey, new KeyPath(0, 0, 0, 0, 0, 0), LabelsArray.Empty, KeyState.Clean));
                smartTx.TryAddWalletOutput(coin);
                outputCoins.Add(coin);
            }

            BlockchainAnalyzer.Analyze(smartTx);
            var wavgInAnon = CoinjoinAnalyzer.WeightedAverage.Invoke(ourCoins.Select(coin => new CoinjoinAnalyzer.AmountWithAnonymity(coin.AnonymitySet, new Money(coin.Amount, MoneyUnit.BTC))));
            var wavgOutAnon = CoinjoinAnalyzer.WeightedAverage.Invoke(outputCoins.Select(coin => new CoinjoinAnalyzer.AmountWithAnonymity(coin.AnonymitySet, new Money(coin.Amount, MoneyUnit.BTC))));


            if (!outputTxOuts.batchedPayments.Any())
            {
                remainingMixReasons.Remove(IWallet.MixingReason.Payment);
                if (wavgOutAnon < wavgInAnon - CoinJoinCoinSelector.MaxWeightedAnonLoss)
                {
                    remainingMixReasons.Remove(IWallet.MixingReason.NotPrivate);
                }
            }
            else if (wavgOutAnon < wavgInAnon)
            {

                remainingMixReasons.Remove(IWallet.MixingReason.NotPrivate);
                remainingMixReasons.Remove(IWallet.MixingReason.ExtraJoin);
            }

            if (remainingMixReasons.Contains(IWallet.MixingReason.Consolidation) && ourOutputsThatAreNotPayments.Count > registeredAliceClients.Length)
            {
                remainingMixReasons.Remove(IWallet.MixingReason.Consolidation);
            }
            

            return remainingMixReasons.Any();
        }

        return (solution.Coins.ToImmutableList(), AcceptableRegistered , AcceptableOutputs);
        
        }
        finally
        {
            if(log.Length > 0)
                _wallet.LogInfo(coordinatorName, $"coinselection: {log}");
            
        }
    }

    private async Task<SubsetSolution> SelectCoinsInternal(
        StringBuilder log,
    string coordinatorName,
    RoundParameters utxoSelectionParameters,
        IEnumerable<SmartCoin> coins,
        IEnumerable<PendingPayment> pendingPayments,
        int maxCoins,
        Dictionary<AnonsetType, int> maxPerType, Dictionary<AnonsetType, int> idealMinimumPerType,
        bool consolidationMode, Money liquidityClue, SecureRandom random)
    {
        // Sort the coins by their anon score and then by descending order their value, and then slightly randomize in 2 ways:
        //attempt to shift coins that comes from the same tx AND also attempt to shift coins based on percentage probability
        var remainingCoins = SlightlyShiftOrder(RandomizeCoins(
            coins.OrderBy(coin => coin.CoinColor(_wallet)).ThenByDescending(x =>
                    x.EffectiveValue(utxoSelectionParameters.MiningFeeRate))
                .ToList(), liquidityClue), 10);
        var remainingPendingPayments = new List<PendingPayment>(pendingPayments);
        var solution = new SubsetSolution(remainingPendingPayments.Count, _wallet,
            utxoSelectionParameters);

        
        solution.ConsolidationMode = consolidationMode;

        while (remainingCoins.Any())
        {
            
            remainingCoins = remainingCoins.Where(coin => !coin.CoinJoinInProgress).ToList();
            if (!remainingCoins.Any())
            {
                break;
            }
            var coinColorCount = solution.SortedCoins.ToDictionary(pair => pair.Key, pair => pair.Value.Length);

            var predicate = new Func<SmartCoin, bool>(_ => true);
            foreach (var coinColor in idealMinimumPerType.ToShuffled(random))
            {
                if (coinColor.Value != 0)
                {
                    coinColorCount.TryGetValue(coinColor.Key, out var currentCoinColorCount);
                    if (currentCoinColorCount < coinColor.Value)
                    {
                        predicate = coin1 => coin1.CoinColor(_wallet) == coinColor.Key;
                        break;
                    }
                }
                else
                {
                    //if the ideal amount = 0, then we should de-prioritize.
                    predicate = coin1 => coin1.CoinColor(_wallet) != coinColor.Key;
                    break;
                }
            }

            var coin = remainingCoins.FirstOrDefault(predicate) ?? remainingCoins.First();
            var color = coin.CoinColor(_wallet);
            // If the selected coins list is at its maximum size, break out of the loop
            if (solution.Coins.Count == maxCoins)
            {
                break;
            }

            remainingCoins.Remove(coin);
            if (maxPerType.TryGetValue(color, out var maxColor) &&
                solution.Coins.Count(coin1 => coin1.CoinColor(_wallet) == color) == maxColor)
            {
                continue;
            }

            solution.Coins.Add(coin);
            // we make sure to spend all coins of the same script as it reduces the chance of the user stupidly consolidating later on
            var scriptPubKey = coin.ScriptPubKey;
            var reusedAddressCoins = remainingCoins.Where(smartCoin => smartCoin.ScriptPubKey == scriptPubKey).ToArray();
            foreach (var reusedAddressCoin in reusedAddressCoins)
            {
                remainingCoins.Remove(reusedAddressCoin);
                solution.Coins.Add(reusedAddressCoin);
            }

            // Loop through the pending payments and handle each payment by subtracting the payment amount from the total value of the selected coins
            var potentialPayments = remainingPendingPayments
                .Where(payment =>
                    payment.ToTxOut().EffectiveCost(utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <=
                    solution.LeftoverValue).ToShuffled(random);

            while (potentialPayments.Any())
            {
                var payment = potentialPayments.First();
                solution.HandledPayments.Add(payment);
                remainingPendingPayments.Remove(payment);
                potentialPayments = remainingPendingPayments.Where(payment =>
                    payment.ToTxOut().EffectiveCost(utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC) <=
                    solution.LeftoverValue).ToShuffled(random);
            }

            if (!remainingPendingPayments.Any())
            {
                //if we're in consolidation mode, we should use more than one coin at the very least
                if (solution.Coins.Count == 1 && consolidationMode)
                {
                    continue;
                }

             
                //if we have less than the max suggested output registration, we should add more coins to reach that number to avoid breaking up into too many coins?
                var isLessThanMaxOutputRegistration = solution.Coins.Count < Math.Max(solution.HandledPayments.Count +1, consolidationMode? 11: 10);
                var rand = Random.Shared.Next(1, 101);
                //let's check how many coins we are allowed to add max and how many we added, and use that percentage as the random chance of not adding it.
                // if max coins = 20, and current coins  = 5 then 5/20 = 0.25 * 100 = 25
                var maxCoinCapacityPercentage = Math.Floor((solution.Coins.Count / (decimal)maxCoins) * 100);
                //aggressively attempt to reach max coin target if consolidation mode is on
                //if we're less than the max output registration, we should be more aggressive in adding coins

                decimal chance = 100;
                if (consolidationMode && !isLessThanMaxOutputRegistration)
                {
                    chance -= maxCoinCapacityPercentage / random.GetInt(2, 8);
                }
                else if (!isLessThanMaxOutputRegistration)
                {
                    chance -= maxCoinCapacityPercentage;
                }

               
                if (chance <= rand)
                {
                    var minDenomAmount = Math.Min(_wallet.MinimumDenominationAmount ?? 0, _wallet.AllowedDenominations?.Any() is true? _wallet.AllowedDenominations.Min(): 0);
                    if (minDenomAmount > 0 && 
                        Money.Coins(solution.LeftoverValue).Satoshi < minDenomAmount)
                    {
                        log.AppendLine($"leftover value {solution.LeftoverValue} is less than minimum denomination amount {minDenomAmount} so we will try to add more coins");
                        continue;
                    }
                    log.AppendLine($"no payments left but at {solution.Coins.Count()} coins. random chance to add another coin if: {chance} > {rand} (random 0-100) continue: {chance > rand}");
                    break;
                }
                
            }
        }
        return solution;
    }

    static List<T> SlightlyShiftOrder<T>(List<T> list, int chanceOfShiftPercentage)
    {
        // Create a random number generator
        var rand = new Random();
        List<T> workingList = new List<T>(list);
// Loop through the coins and determine whether to swap the positions of two consecutive coins in the list
        for (int i = 0; i < workingList.Count() - 1; i++)
        {
            // If a random number between 0 and 1 is less than or equal to 0.1, swap the positions of the current and next coins in the list
            if (rand.NextDouble() <= ((double)chanceOfShiftPercentage / 100))
            {
                // Swap the positions of the current and next coins in the list
                (workingList[i], workingList[i + 1]) = (workingList[i + 1], workingList[i]);
            }
        }

        return workingList;
    }

    private List<SmartCoin> RandomizeCoins(List<SmartCoin> coins, Money liquidityClue)
    {
        var remainingCoins = new List<SmartCoin>(coins);
        var workingList = new List<SmartCoin>();
        while (remainingCoins.Any())
        {
            var currentCoin = remainingCoins.First();
            remainingCoins.RemoveAt(0);
            var lastCoin = workingList.LastOrDefault();
            if (lastCoin is null || currentCoin.CoinColor(_wallet) == AnonsetType.Green ||
                !remainingCoins.Any() ||
                (remainingCoins.Count == 1 && remainingCoins.First().TransactionId == currentCoin.TransactionId) ||
                lastCoin.TransactionId != currentCoin.TransactionId ||
                liquidityClue <= currentCoin.Amount ||
                Random.Shared.Next(0, 10) < 5)
            {
                workingList.Add(currentCoin);
            }
            else
            {
                remainingCoins.Insert(1, currentCoin);
            }
        }


        return workingList.ToList();
    }
}

public static class SmartCoinExtensions
{
    public static AnonsetType CoinColor(this SmartCoin coin, IWallet wallet)
    {
        return coin.IsPrivate(wallet)? AnonsetType.Green: coin.IsSemiPrivate(wallet)? AnonsetType.Orange: AnonsetType.Red;
    }
}

public enum AnonsetType
{
    Red,
    Orange,
    Green
}

public class SubsetSolution
{
    private readonly RoundParameters _utxoSelectionParameters;

    public SubsetSolution(int totalPaymentsGross, IWallet wallet, RoundParameters utxoSelectionParameters)
    {
        _utxoSelectionParameters = utxoSelectionParameters;
        TotalPaymentsGross = totalPaymentsGross;
        Wallet = wallet;
    }
    public List<SmartCoin> Coins { get; set; } = new();
    public List<PendingPayment> HandledPayments { get; set; } = new();

    public decimal TotalValue => Coins.Sum(coin => coin.EffectiveValue(_utxoSelectionParameters.MiningFeeRate)
            .ToDecimal(MoneyUnit.BTC));

    public Dictionary<AnonsetType, SmartCoin[]> SortedCoins =>
        Coins.GroupBy(coin => coin.CoinColor(Wallet)).ToDictionary(coins => coins.Key, coins => coins.ToArray());

    public int TotalPaymentsGross { get; }
    public IWallet Wallet { get; }

    public decimal TotalPaymentCost => HandledPayments.Sum(payment =>
        payment.ToTxOut().EffectiveCost(_utxoSelectionParameters.MiningFeeRate).ToDecimal(MoneyUnit.BTC));

    public decimal LeftoverValue => TotalValue - TotalPaymentCost;
    public bool ConsolidationMode { get; set; }

    public override string ToString()
    {
        if (!Coins.Any())
        {
            return "Solution yielded no selection of coins";
        }

        var sc = SortedCoins;
        sc.TryGetValue(AnonsetType.Green, out var gcoins);
        sc.TryGetValue(AnonsetType.Orange, out var ocoins);
        sc.TryGetValue(AnonsetType.Red, out var rcoins);

        
        return $"Selected {Coins.Count} ({TotalValue} BTC) ({ocoins?.Length +  rcoins?.Length + 0} not private, {gcoins?.Length ?? 0} private) coins to pay {TotalPaymentsGross} payments ({TotalPaymentCost} BTC) with {LeftoverValue} BTC leftover\n Consolidation mode:{ConsolidationMode}";
    }
}
