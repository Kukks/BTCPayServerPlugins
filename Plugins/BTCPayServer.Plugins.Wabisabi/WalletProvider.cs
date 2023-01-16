using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Payments.PayJoin;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using WalletWasabi.Bases;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;

public class WalletProvider : PeriodicRunner,IWalletProvider
{
    private Dictionary<string, WabisabiStoreSettings>? _cachedSettings;
    private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;
    private readonly IExplorerClientProvider _explorerClientProvider;
    public IUTXOLocker UtxoLocker { get; set; }
    private readonly ILoggerFactory _loggerFactory;

    public WalletProvider(IStoreRepository storeRepository, IBTCPayServerClientFactory btcPayServerClientFactory,
        IExplorerClientProvider explorerClientProvider, ILoggerFactory loggerFactory, IUTXOLocker utxoLocker ) : base(TimeSpan.FromMinutes(5))
    {
        UtxoLocker = utxoLocker;
        _btcPayServerClientFactory = btcPayServerClientFactory;
        _explorerClientProvider = explorerClientProvider;
        _loggerFactory = loggerFactory;
        initialLoad = Task.Run(async () =>
        {
            _cachedSettings =
                await storeRepository.GetSettingsAsync<WabisabiStoreSettings>(nameof(WabisabiStoreSettings));
        });
    }

    public readonly  ConcurrentDictionary<string, Task<IWallet?>> LoadedWallets = new();
    public ConcurrentDictionary<string, Dictionary<OutPoint, DateTimeOffset>> BannedCoins = new();

   
    public class WalletUnloadEventArgs : EventArgs
    {
        public string Wallet { get; }

        public WalletUnloadEventArgs(string wallet)
        {
            Wallet = wallet;
        }
    }

    public event EventHandler<WalletUnloadEventArgs>? WalletUnloaded;
    public async Task<IWallet> GetWalletAsync(string name)
    {
        await initialLoad;
        return await LoadedWallets.GetOrAddAsync(name, async s =>
        {

            if (!_cachedSettings.TryGetValue(name, out var wabisabiStoreSettings))
            {
                return null;
            }
            
            var client = await _btcPayServerClientFactory.Create(null, name);
            var pm = await client.GetStoreOnChainPaymentMethod(name, "BTC");
            var explorerClient = _explorerClientProvider.GetExplorerClient("BTC");
            var derivationStrategy =
                explorerClient.Network.DerivationStrategyFactory.Parse(pm.DerivationScheme);

            var masterKey = await explorerClient.GetMetadataAsync<BitcoinExtKey>(derivationStrategy,
                WellknownMetadataKeys.MasterHDKey);
            var accountKey = await explorerClient.GetMetadataAsync<BitcoinExtKey>(derivationStrategy,
                WellknownMetadataKeys.AccountHDKey);

            var keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, masterKey, accountKey);

            var destinationProvider =
                new NBXInternalDestinationProvider(explorerClient, _btcPayServerClientFactory, derivationStrategy, client, name,
                    wabisabiStoreSettings);

            var smartifier = new Smartifier(explorerClient, derivationStrategy, _btcPayServerClientFactory, name,
                CoinOnPropertyChanged);

            return (IWallet)new BTCPayWallet(pm, derivationStrategy, explorerClient, keychain, destinationProvider,
                _btcPayServerClientFactory, name, wabisabiStoreSettings, UtxoLocker,
                _loggerFactory, smartifier, BannedCoins);
            
        });
        
    }

    private Task initialLoad = null;
    public async Task<IEnumerable<IWallet>> GetWalletsAsync()
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient("BTC");
        var status = await explorerClient.GetStatusAsync();
        if (!status.IsFullySynched)
        {
            return Array.Empty<IWallet>();
        }

        await initialLoad; 
        return (await Task.WhenAll(_cachedSettings
                .Select(pair => GetWalletAsync(pair.Key))))
            .Where(wallet => wallet is not null);
    }

    private void CoinOnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (sender is SmartCoin smartCoin)
        {
            if (e.PropertyName == nameof(SmartCoin.CoinJoinInProgress))
            {
                // _logger.LogInformation($"{smartCoin.Outpoint}.CoinJoinInProgress = {smartCoin.CoinJoinInProgress}");
                if (UtxoLocker is not null)
                {
                    _ = (smartCoin.CoinJoinInProgress
                        ? UtxoLocker.TryLock(smartCoin.Outpoint)
                        : UtxoLocker.TryUnlock(smartCoin.Outpoint)).ContinueWith(task =>
                    {
                        // _logger.LogInformation(
                        //     $"{(task.Result ? "Success" : "Fail")}: {(smartCoin.CoinJoinInProgress ? "" : "un")}locking coin for coinjoin: {smartCoin.Outpoint} ");
                    });
                }
            }
        }
    }

    public async Task ResetWabisabiStuckPayouts()
    {
        var wallets = await GetWalletsAsync();
        foreach (BTCPayWallet wallet in wallets)
        {
            var client = await _btcPayServerClientFactory.Create(null, wallet.StoreId);
            var payouts = await client.GetStorePayouts(wallet.StoreId);
            var inProgressPayouts = payouts.Where(data =>
                data.State == PayoutState.InProgress && data.PaymentMethod == "BTC" &&
                data.PaymentProof?.Value<string>("proofType") == "Wabisabi");
            foreach (PayoutData payout in inProgressPayouts)
            {
                try
                {
                    var paymentproof =
                        payout.PaymentProof.ToObject<NBXInternalDestinationProvider.WabisabiPaymentProof>();
                    if (paymentproof.Candidates?.Any() is not true)
                        await client.MarkPayout(wallet.StoreId, payout.Id,
                            new MarkPayoutRequest() {State = PayoutState.AwaitingPayment});
                }
                catch (Exception e)
                {
                }
            }
        }
    }

    protected override async Task ActionAsync(CancellationToken cancel)
    {

        var toCheck = LoadedWallets.Keys.ToList();
        while (toCheck.Any())
        {
            var storeid = toCheck.First();
            await Check(storeid, cancel);
            toCheck.Remove(storeid);
        }
    }

    public async Task Check(string storeId, CancellationToken cancellationToken)
    {
        var client = await _btcPayServerClientFactory.Create(null, storeId);
        try
        {
            if (LoadedWallets.TryGetValue(storeId, out var currentWallet))
            {
                var wallet = (BTCPayWallet)await currentWallet;
                var kc = (BTCPayKeyChain)wallet.KeyChain;
                var pm = await client.GetStoreOnChainPaymentMethod(storeId, "BTC", cancellationToken);
                if (pm.DerivationScheme != wallet.OnChainPaymentMethodData.DerivationScheme)
                {
                    await UnloadWallet(storeId);
                }
                else
                {
                    wallet.OnChainPaymentMethodData = pm;
                }

                if (!kc.KeysAvailable)
                {
                    await UnloadWallet(storeId);
                }
            }
        }
        catch (Exception e)
        {
            await UnloadWallet(storeId);
        }
    }

    private async Task UnloadWallet(string name)
    {
        
        LoadedWallets.TryRemove(name, out _);
        WalletUnloaded?.Invoke(this, new WalletUnloadEventArgs(name));
    }

    public async Task SettingsUpdated(string storeId, WabisabiStoreSettings wabisabiSettings)
    {
           
        if (wabisabiSettings.Settings.All(settings => !settings.Enabled))
        {
            _cachedSettings?.Remove(storeId);
            await UnloadWallet(storeId);
        }else if (LoadedWallets.TryGetValue(storeId, out var existingWallet))
        {
            
            _cachedSettings.AddOrReplace(storeId, wabisabiSettings);
            var btcpayWalet = (BTCPayWallet) await existingWallet;
            if (btcpayWalet is null)
            {
                
                LoadedWallets.TryRemove(storeId, out _);
                var w = await GetWalletAsync(storeId);
                if (w is null)
                {
                    await UnloadWallet(storeId);
                }
            }
            else
            {
                
                btcpayWalet.WabisabiStoreSettings = wabisabiSettings;
            }
        }
        else
        {
            _cachedSettings.AddOrReplace(storeId, wabisabiSettings);
            await GetWalletAsync(storeId);
        }
    }

    public void OnBan(string coordinatorName, BannedCoinEventArgs args)
    {
        BannedCoins.AddOrUpdate(coordinatorName,
            s => new Dictionary<OutPoint, DateTimeOffset>() {{args.Utxo, args.BannedTime}},
            (s, offsets) =>
            {
                offsets.TryAdd(args.Utxo, args.BannedTime);
                return offsets;
            });
    }
}
