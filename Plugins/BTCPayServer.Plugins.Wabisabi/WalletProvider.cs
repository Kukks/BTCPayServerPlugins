using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using WalletWasabi.Bases;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.Wabisabi;

public class WalletProvider : PeriodicRunner, IWalletProvider
{
    private ConcurrentDictionary<string, WabisabiStoreSettings>? _cachedSettings;
    private readonly IServiceProvider _serviceProvider;
    private readonly StoreRepository _storeRepository;
    private readonly IExplorerClientProvider _explorerClientProvider;
    public IUTXOLocker UtxoLocker { get; set; }
    private readonly ILoggerFactory _loggerFactory;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<WalletProvider> _logger;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private readonly IMemoryCache _memoryCache;

    public readonly ConcurrentDictionary<string, Lazy<Task<BTCPayWallet>>> LoadedWallets = new();

    private readonly TaskCompletionSource _initialLoad = new();
    private readonly CompositeDisposable _disposables = new();

    public WalletProvider(
        IServiceProvider serviceProvider,
        StoreRepository storeRepository,
        IExplorerClientProvider explorerClientProvider,
        ILoggerFactory loggerFactory,
        IUTXOLocker utxoLocker,
        EventAggregator eventAggregator,
        ILogger<WalletProvider> logger,
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        IMemoryCache memoryCache) : base(TimeSpan.FromMinutes(5))
    {
        UtxoLocker = utxoLocker;
        _serviceProvider = serviceProvider;
        _storeRepository = storeRepository;
        _explorerClientProvider = explorerClientProvider;
        _loggerFactory = loggerFactory;
        _eventAggregator = eventAggregator;
        _logger = logger;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _memoryCache = memoryCache;
    }
    public async Task<BTCPayWallet?> GetWalletAsync(string name)
    {
        await _initialLoad.Task;
        return await Smartifier.GetOrCreate(LoadedWallets, name, async () =>
        {
            if (!_cachedSettings.TryGetValue(name, out var wabisabiStoreSettings))
            {
                return null;
            }
            var store = await _storeRepository.FindStore(name);
            var paymentMethod = store?.GetDerivationSchemeSettings(_paymentMethodHandlerDictionary, "BTC");
            if (paymentMethod is null)
            {
                return null;
            }

            var pmi = Payments.PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
            var explorerClient = _explorerClientProvider.GetExplorerClient("BTC");
            var isHotWallet = paymentMethod.IsHotWallet;
            var enabled = store.GetEnabledPaymentIds().Contains(pmi);
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
                var smartifier = new Smartifier(_memoryCache, _logger,
                    _serviceProvider.GetRequiredService<WalletRepository>(),
                    explorerClient, derivationStrategy, name, UtxoLocker, accountKeyPath);
                if (masterKey is null || accountKey is null || accountKeyPath is null)
                {
                    keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, null, null, smartifier);
                }
                else
                    keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, masterKey, accountKey,
                        smartifier);
            }
            else
            {
                var smartifier = new Smartifier(_memoryCache, _logger,
                    _serviceProvider.GetRequiredService<WalletRepository>(), explorerClient,
                    derivationStrategy, name, UtxoLocker, accountKeyPath);
                keychain = new BTCPayKeyChain(explorerClient, derivationStrategy, null, null, smartifier);
            }

            var payoutMethodId = PayoutTypes.CHAIN.GetPayoutMethodId("BTC");

            return new BTCPayWallet(
                _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>(),
                _serviceProvider.GetRequiredService<WalletRepository>(),
                _serviceProvider.GetRequiredService<BTCPayNetworkProvider>(),
                _serviceProvider.GetRequiredService<PayoutMethodHandlerDictionary>()[payoutMethodId],
                _serviceProvider.GetRequiredService<BTCPayNetworkJsonSerializerSettings>(),
                _serviceProvider.GetRequiredService<Services.Wallets.BTCPayWalletProvider>().GetWallet("BTC"),
                _serviceProvider.GetRequiredService<PullPaymentHostedService>(), derivationStrategy, explorerClient,
                keychain,
                name, wabisabiStoreSettings, UtxoLocker,
                _loggerFactory,
                _serviceProvider.GetRequiredService<StoreRepository>(),
                _serviceProvider.GetRequiredService<IMemoryCache>()
            );
        }, _logger);
    }

    public async Task<IEnumerable<IWallet>> GetWalletsAsync()
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient("BTC");
        var status = await explorerClient.GetStatusAsync();
        if (!status.IsFullySynched)
        {
            return Array.Empty<IWallet>();
        }

        await _initialLoad.Task;
        return (await Task.WhenAll(_cachedSettings
                .Select(pair => GetWalletAsync(pair.Key))))
            .Where(wallet => wallet is not null);
    }


    public async Task ResetWabisabiStuckPayouts(string[] storeIds)
    {
        await _initialLoad.Task;


        storeIds ??= _cachedSettings?.Keys.ToArray() ?? Array.Empty<string>();
        if (!storeIds.Any())
        {
            return;
        }

        var pullPaymentHostedService = _serviceProvider.GetRequiredService<PullPaymentHostedService>();
        var payouts = await pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
        {
            States = new[]
            {
                PayoutState.InProgress
            },
            PayoutMethods = new[] {PayoutTypes.CHAIN.GetPayoutMethodId("BTC").ToString()},
            Stores = storeIds
        });
        var inProgressPayouts = payouts
            .Where(data => data.GetProofBlobJson()?.Value<string>("proofType") == "Wabisabi").ToArray();
        if (!inProgressPayouts.Any())
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
                if (_cachedSettings.TryGetValue(storeId, out var settings) &&
                    settings.Settings.Any(coordinatorSettings => coordinatorSettings.Enabled))
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
    }

    public async Task SettingsUpdated(string storeId, WabisabiStoreSettings wabisabiSettings)
    {
        _cachedSettings.AddOrReplace(storeId, wabisabiSettings);

        if (!wabisabiSettings.Active || wabisabiSettings.Settings.All(settings => !settings.Enabled))
        {
            await UnloadWallet(storeId);
        }

        if (LoadedWallets.TryGetValue(storeId, out var existingWallet))
        {
            var btcpayWallet = await existingWallet.Value;
            if (btcpayWallet is null)
            {
                LoadedWallets.TryRemove(storeId, out _);
            }
            else
            {
                btcpayWallet.WabisabiStoreSettings = wabisabiSettings;
            }
        }

        var w = await GetWalletAsync(storeId);
        if (w is null)
        {
            await UnloadWallet(storeId);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            _cachedSettings = new ConcurrentDictionary<string, WabisabiStoreSettings>(
                (await _storeRepository.GetSettingsAsync<WabisabiStoreSettings>(nameof(WabisabiStoreSettings))));
            _initialLoad.SetResult();
        }, cancellationToken);
        _disposables.Add(_eventAggregator.SubscribeAsync<StoreRemovedEvent>(async @event =>
        {
            await _initialLoad.Task;
            await UnloadWallet(@event.StoreId);
        }));
        _disposables.Add(_eventAggregator.SubscribeAsync<WalletChangedEvent>(async @event =>
        {
            if (@event.WalletId.CryptoCode == "BTC")
            {
                await _initialLoad.Task;
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