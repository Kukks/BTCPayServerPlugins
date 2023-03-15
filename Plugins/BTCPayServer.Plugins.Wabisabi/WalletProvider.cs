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
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using WalletWasabi.Bases;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.Wabisabi;

public class WalletProvider : PeriodicRunner,IWalletProvider
{
    private Dictionary<string, WabisabiStoreSettings>? _cachedSettings;
    private readonly IServiceProvider _serviceProvider;
    private readonly StoreRepository _storeRepository;
    private readonly IExplorerClientProvider _explorerClientProvider;
    public IUTXOLocker UtxoLocker { get; set; }
    private readonly ILoggerFactory _loggerFactory;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<WalletProvider> _logger;
    private readonly BTCPayNetworkProvider _networkProvider;

    public WalletProvider(
        IServiceProvider serviceProvider,
        StoreRepository storeRepository, 
        IExplorerClientProvider explorerClientProvider, 
        ILoggerFactory loggerFactory, 
        IUTXOLocker utxoLocker, 
        EventAggregator eventAggregator,
        ILogger<WalletProvider> logger,
        BTCPayNetworkProvider networkProvider) : base(TimeSpan.FromMinutes(5))
    {
        UtxoLocker = utxoLocker;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _explorerClientProvider = explorerClientProvider;
        _loggerFactory = loggerFactory;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _networkProvider = networkProvider;
    }

    public readonly  ConcurrentDictionary<string, Task<IWallet?>> LoadedWallets = new();
    public ConcurrentDictionary<string, Dictionary<OutPoint, DateTimeOffset>> BannedCoins = new();

   
    public class WalletUnloadEventArgs : EventArgs
    {
        public IWallet Wallet { get; }

        public WalletUnloadEventArgs(IWallet wallet)
        {
            Wallet = wallet;
        }
    }

    public event EventHandler<WalletUnloadEventArgs>? WalletUnloaded;
    public async Task<IWallet?> GetWalletAsync(string name)
    {
        await initialLoad.Task;
        return await LoadedWallets.GetOrAddAsync(name, async s =>
        {
            if (!_cachedSettings.TryGetValue(name, out var wabisabiStoreSettings))
            {
                return null;
            }
            var store = await _storeRepository.FindStore(name);
            var paymentMethod = store?.GetDerivationSchemeSettings(_networkProvider, "BTC");
            if (paymentMethod is null)
            {
                return null;
            }

            var explorerClient = _explorerClientProvider.GetExplorerClient("BTC");
            var isHotWallet = paymentMethod.IsHotWallet;
            var enabled = store.GetEnabledPaymentIds(_networkProvider).Contains(paymentMethod.PaymentId);
            var derivationStrategy = paymentMethod.AccountDerivation;
            BTCPayKeyChain keychain;
            var accountKeyPath = paymentMethod.AccountKeySettings.FirstOrDefault()?.GetRootedKeyPath();
            if (isHotWallet && enabled)
            {
                var masterKey = await explorerClient.GetMetadataAsync<BitcoinExtKey>(derivationStrategy,
                    WellknownMetadataKeys.MasterHDKey);
                var accountKey = await explorerClient.GetMetadataAsync<BitcoinExtKey>(derivationStrategy,
                    WellknownMetadataKeys.AccountHDKey);
                    var accountKeyPath2 = await explorerClient.GetMetadataAsync<RootedKeyPath>(derivationStrategy,
                    WellknownMetadataKeys.AccountKeyPath);
                    accountKeyPath = accountKeyPath2 ?? accountKeyPath;
                    var smartifier = new Smartifier(_serviceProvider.GetRequiredService<WalletRepository>(),
                        explorerClient, derivationStrategy, name, UtxoLocker, accountKeyPath);
                if (masterKey is null || accountKey is null || accountKeyPath is null)
                {
                    keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, null, null, smartifier);
                }else
                    keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, masterKey, accountKey, smartifier);

            }
            else
            {
                var smartifier = new Smartifier(_serviceProvider.GetRequiredService<WalletRepository>(), explorerClient,
                    derivationStrategy, name, UtxoLocker, accountKeyPath);
                keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, null, null, smartifier);
            }


            return (IWallet)new BTCPayWallet(
                _serviceProvider.GetRequiredService<WalletRepository>(),
                _serviceProvider.GetRequiredService<BTCPayNetworkProvider>(),
                _serviceProvider.GetRequiredService<BitcoinLikePayoutHandler>(),
                _serviceProvider.GetRequiredService<BTCPayNetworkJsonSerializerSettings>(),
                _serviceProvider.GetRequiredService<Services.Wallets.BTCPayWalletProvider>().GetWallet("BTC"),
                _serviceProvider.GetRequiredService<PullPaymentHostedService>(),derivationStrategy, explorerClient, keychain,
                name, wabisabiStoreSettings, UtxoLocker,
                _loggerFactory, 
                _serviceProvider.GetRequiredService<StoreRepository>(), BannedCoins,
                _eventAggregator);
            
        });
        
    }

    private TaskCompletionSource initialLoad = new();
    private CompositeDisposable _disposables = new();

    public async Task<IEnumerable<IWallet>> GetWalletsAsync()
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient("BTC");
        var status = await explorerClient.GetStatusAsync();
        if (!status.IsFullySynched)
        {
            return Array.Empty<IWallet>();
        }

        await initialLoad.Task; 
        return (await Task.WhenAll(_cachedSettings
                .Select(pair => GetWalletAsync(pair.Key))))
            .Where(wallet => wallet is not null);
    }

    

    public async Task ResetWabisabiStuckPayouts(string[] storeIds)
    {

        await initialLoad.Task;
        
        
        storeIds??= _cachedSettings?.Keys.ToArray() ?? Array.Empty<string>();
        if (!storeIds.Any())
        {
            return;
        }
        var pullPaymentHostedService =  _serviceProvider.GetRequiredService<PullPaymentHostedService>();
        var payouts = await pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
        {
            States = new[]
            {
                PayoutState.InProgress
            },
            PaymentMethods = new[] {"BTC"},
            Stores = storeIds
        });
        var inProgressPayouts = payouts
            .Where(data => data.GetProofBlobJson()?.Value<string>("proofType") == "Wabisabi").ToArray();
        if(!inProgressPayouts.Any())
            return;
        _logger.LogInformation("Moving {count} stuck coinjoin payouts to AwaitingPayment", inProgressPayouts.Length);
        foreach (PayoutData payout in inProgressPayouts)
        {
            try
            {
                await pullPaymentHostedService.MarkPaid(new HostedServices.MarkPayoutRequest()
                {
                    State = PayoutState.AwaitingPayment,
                    PayoutId = payout.Id
                });
            }
            catch (Exception e)
            {
            }
        }
    }

    protected override async Task ActionAsync(CancellationToken cancel)
    {

        // var toCheck = LoadedWallets.Keys.ToList();
        // while (toCheck.Any())
        // {
        //     var storeid = toCheck.First();
        //     await Check(storeid, cancel);
        //     toCheck.Remove(storeid);
        // }
    }

    public async Task Check(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            if (LoadedWallets.TryGetValue(storeId, out var currentWallet))
            {
                await UnloadWallet(storeId);
                if(_cachedSettings.TryGetValue(storeId , out var settings) && settings.Settings.Any(coordinatorSettings => coordinatorSettings.Enabled))
                    await GetWalletAsync(storeId);
                await GetWalletAsync(storeId);
            }
        }
        catch (Exception e)
        {
            await UnloadWallet(storeId);
        }
    }

    private async Task UnloadWallet(string name)
    {
        
        LoadedWallets.TryRemove(name, out var walletTask);
        if (walletTask != null)
        {
            var wallet = await walletTask;
            WalletUnloaded?.Invoke(this, new WalletUnloadEventArgs(wallet));
        }
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
    
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            _cachedSettings =
                await _storeRepository.GetSettingsAsync<WabisabiStoreSettings>(nameof(WabisabiStoreSettings));
            initialLoad.SetResult();
        }, cancellationToken);
        _disposables.Add(_eventAggregator.SubscribeAsync<StoreRemovedEvent>(async @event =>
        {
            await initialLoad.Task;
            await UnloadWallet(@event.StoreId);

        }));
        _disposables.Add(_eventAggregator.SubscribeAsync<WalletChangedEvent>(async @event =>
        {
            if (@event.WalletId.CryptoCode == "BTC")
            {
                await initialLoad.Task;
                await Check(@event.WalletId.StoreId, cancellationToken);
            }

        }));
                
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _disposables?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
