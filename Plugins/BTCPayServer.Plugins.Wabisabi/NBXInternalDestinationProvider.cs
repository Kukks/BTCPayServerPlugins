using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using NBitcoin;
using NBitcoin.Payment;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WalletWasabi.Extensions;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Client;

namespace BTCPayServer.Plugins.Wabisabi;

public class NBXInternalDestinationProvider : IDestinationProvider
{
    private readonly ExplorerClient _explorerClient;
    private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
    private readonly DerivationStrategyBase _derivationStrategy;
    private readonly BTCPayServerClient _client;
    private readonly string _storeId;
    private readonly WabisabiStoreSettings _wabisabiStoreSettings;

    public NBXInternalDestinationProvider(ExplorerClient explorerClient, IBTCPayServerClientFactory btcPayServerClientFactory,
        DerivationStrategyBase derivationStrategy, BTCPayServerClient client, string storeId, WabisabiStoreSettings wabisabiStoreSettings)
    {
        _explorerClient = explorerClient;
        _btcPayServerClientFactory = btcPayServerClientFactory;
        _derivationStrategy = derivationStrategy;
        _client = client;
        _storeId = storeId;
        _wabisabiStoreSettings = wabisabiStoreSettings;
    }

    public async Task<IEnumerable<IDestination>> GetNextDestinationsAsync(int count, bool preferTaproot, bool mixedOutputs)
    {
        if (!_wabisabiStoreSettings.PlebMode && !string.IsNullOrEmpty(_wabisabiStoreSettings.MixToOtherWallet) && mixedOutputs)
        {
            try
            {
                var mixClient = await _btcPayServerClientFactory.Create(null, _wabisabiStoreSettings.MixToOtherWallet);
                var pm = await mixClient.GetStoreOnChainPaymentMethod(_wabisabiStoreSettings.MixToOtherWallet,
                    "BTC");
                
               var deriv =   _explorerClient.Network.DerivationStrategyFactory.Parse(pm.DerivationScheme);
               if (deriv.ScriptPubKeyType() == _derivationStrategy.ScriptPubKeyType())
               {
                   
                   return  await  Task.WhenAll(Enumerable.Repeat(0, count).Select(_ =>
                       _explorerClient.GetUnusedAsync(deriv, DerivationFeature.Deposit, 0, true))).ContinueWith(task => task.Result.Select(information => information.Address));
               }
            }
            
            catch (Exception e)
            {
                _wabisabiStoreSettings.MixToOtherWallet = null;
            }
        }
        return  await  Task.WhenAll(Enumerable.Repeat(0, count).Select(_ =>
            _explorerClient.GetUnusedAsync(_derivationStrategy, DerivationFeature.Deposit, 0, true))).ContinueWith(task => task.Result.Select(information => information.Address));
    }

    public async Task<IEnumerable<PendingPayment>> GetPendingPaymentsAsync( UtxoSelectionParameters roundParameters)
    {
        try
        {
           var payouts = await _client.GetStorePayouts(_storeId, false);
           return payouts.Where(data =>
               data.State == PayoutState.AwaitingPayment &&
               data.PaymentMethod.Equals("BTC", StringComparison.InvariantCultureIgnoreCase)).Select(data =>
           {
               IDestination destination = null;
               try
               {

                   var bip21 = new BitcoinUrlBuilder(data.Destination, _explorerClient.Network.NBitcoinNetwork);
                   destination = bip21.Address;
               }
               catch (Exception e)
               {
                   destination = BitcoinAddress.Create(data.Destination, _explorerClient.Network.NBitcoinNetwork);
               }

               var value = new Money((decimal)data.PaymentMethodAmount, MoneyUnit.BTC);
               if (!roundParameters.AllowedOutputAmounts.Contains(value) ||
                   !roundParameters.AllowedOutputScriptTypes.Contains(destination.ScriptPubKey.GetScriptType()))
               {
                   return null;
               }
               return new PendingPayment()
               {
                   Identifier = data.Id,
                   Destination = destination,
                   Value =value,
                   PaymentStarted = PaymentStarted(_storeId, data.Id),
                   PaymentFailed = PaymentFailed(_storeId, data.Id),
                   PaymentSucceeded = PaymentSucceeded(_storeId, data.Id,
                       _explorerClient.Network.NBitcoinNetwork.ChainName == ChainName.Mainnet),
               };
           }).Where(payment => payment is not null).ToArray();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return Array.Empty<PendingPayment>();
        }
    }

    private Action<(uint256 roundId, uint256 transactionId, int outputIndex)> PaymentSucceeded(string storeId, string payoutId, bool mainnet)
    {
        return tuple =>
            _client.MarkPayout(storeId, payoutId, new MarkPayoutRequest()
            {
                State = PayoutState.InProgress,
                PaymentProof = JObject.FromObject(new WabisabiPaymentProof()
                { 
                    ProofType = "PayoutTransactionOnChainBlob",
                    Candidates = new HashSet<uint256>()
                    {
                        tuple.transactionId
                    },
                    TransactionId = tuple.transactionId,
                    Link = ComputeTxUrl(mainnet, tuple.transactionId.ToString(), tuple.outputIndex.ToString())
                })
            });

    }

    public static string ComputeTxUrl(bool mainnet,  string tx, string outputIndex = null)
    {
        var path = $"tx/{(outputIndex is null ? tx : $"{outputIndex}:{tx}#flow")}";
        return mainnet
            ? $"https://mempool.space/{path}"
            : $"https://mempool.space/testnet/{path}";
    }
    private Action PaymentFailed(string storeId, string payoutId)
    {
        return () =>
            _client.MarkPayout(storeId, payoutId, new MarkPayoutRequest() {State = PayoutState.AwaitingPayment});
    }

    private Func<Task<bool>> PaymentStarted(string storeId, string payoutId)
    {
        return async () =>
        {
            try
            {
                await _client.MarkPayout(storeId, payoutId,
                    new MarkPayoutRequest()
                    {
                        State = PayoutState.InProgress,
                        PaymentProof = JObject.FromObject(new WabisabiPaymentProof())
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
