using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.Prism.ViewModel;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace BTCPayServer.Plugins.Prism.Services;

public class AutoTransferService : EventHostedServiceBase, IPeriodicTask
{
    private readonly string _network = "BTC";
    private readonly StoreRepository _storeRepo;
    private readonly StoreRepository _storeRepository;
    private readonly EventAggregator _eventAggregator;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly ILogger<AutoTransferService> _logger;
    private readonly IPluginHookService _pluginHookService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly WalletReceiveService _walletReceiveService;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    public event EventHandler<AutoTransferPaymentEvent> AutoTransferUpdated;

    public AutoTransferService(StoreRepository storeRepo,
        StoreRepository storeRepository,
        EventAggregator eventAggregator,
        BTCPayWalletProvider walletProvider,
        ILogger<AutoTransferService> logger,
        IPluginHookService pluginHookService,
        WalletReceiveService walletReceiveService,
        PullPaymentHostedService pullPaymentHostedService,
        PaymentMethodHandlerDictionary handlers) : base(eventAggregator, logger)
    {
        _logger = logger;
        _handlers = handlers;
        _storeRepo = storeRepo;
        _walletProvider = walletProvider;
        _eventAggregator = eventAggregator;
        _storeRepository = storeRepository;
        _pluginHookService = pluginHookService;
        _walletReceiveService = walletReceiveService;
        _pullPaymentHostedService = pullPaymentHostedService;

    }

    public async Task Do(CancellationToken cancellationToken)
    {
        try
        {
            var autoSettings = await GetAllAutoTransferSettings();
            foreach (var setting in autoSettings)
            {
                if (setting.Value != null)
                {
                    if (!setting.Value.Enabled || !setting.Value.EnableScheduledAutomation || !setting.Value.ScheduledDestinations.Any()) return;

                    PushEvent(new AutoTransferPaymentEvent { StoreId = setting.Key, Settings = setting.Value });
                }
            }
        }
        catch (PostgresException)
        {
            Logs.PayServer.LogInformation("Skipping task: An error occured.");
        }
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is AutoTransferPaymentEvent sequentialExecute) await HandleSchedulePayment(sequentialExecute.StoreId);

        await base.ProcessEvent(evt, cancellationToken);
    }

    private async Task HandleSchedulePayment(string storeId)
    {
        var settings = await GetAutoTransferSettings(storeId);
        if (!settings.Enabled || !settings.EnableScheduledAutomation || !settings.ScheduledDestinations.Any()) return;

        var todayDate = DateTime.UtcNow.Date;
        var reminders = settings.AutomationTransferDays.Split(',').Select(int.Parse).ToList();
        if (reminders.Contains(todayDate.Day))
        {
            foreach (var schedule in settings.ScheduledDestinations)
            {
                await CreatePayouts(storeId, new AutoTransferSettingsViewModel
                {
                    Destinations = schedule.Value,
                    ReserveFeePercentage = settings.Reserve,
                    SatThreshold = settings.SatThreshold
                });

            }
        }
    }

    public async Task WaitAndLock(CancellationToken cancellationToken = default)
    {
        await _updateLock.WaitAsync(cancellationToken);
    }

    public void Unlock()
    {
        _updateLock.Release();
    }

    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public async Task<AutoTransferSettings> GetAutoTransferSettings(string storeId)
    {
        var autoTransferSettingsDict = await _storeRepository.GetSettingsAsync<AutoTransferSettings>(nameof(AutoTransferSettings));
        return autoTransferSettingsDict.TryGetValue(storeId, out var autoTransferSettings) ? autoTransferSettings : new AutoTransferSettings();
    }

    public async Task<Dictionary<string, AutoTransferSettings>> GetAllAutoTransferSettings()
    {
        return await _storeRepository.GetSettingsAsync<AutoTransferSettings>(nameof(AutoTransferSettings));
    }

    public async Task<bool> UpdateAutoTransferSettingsForStore(string storeId, AutoTransferSettings updatedSettings, bool skipLock = false)
    {
        try
        {
            if (!skipLock)
                await WaitAndLock();
            var currentSettings = await GetAutoTransferSettings(storeId);
            await _storeRepository.UpdateSetting(storeId, nameof(AutoTransferSettings), updatedSettings);
        }
        finally
        {
            if (!skipLock)
                Unlock();
        }
        var autoTransferPaymentDetectedEventArgs = new AutoTransferPaymentEvent()
        {
            StoreId = storeId,
            Settings = updatedSettings
        };
        AutoTransferUpdated?.Invoke(this, autoTransferPaymentDetectedEventArgs);
        return true;
    }

    public async Task<(bool success, string message)> CreatePayouts(string storeId, AutoTransferSettingsViewModel autoPayoutSettings)
    {
        var autoTransferSettings = await GetAutoTransferSettings(storeId);
        if (!autoTransferSettings.Enabled) return(false, "Enable auto-transfer to process payment");

        var currentStore = await _storeRepo.FindStore(storeId);
        var walletBalance = await GetStoreWalletBalance(currentStore);
        var thresholdBalance = Math.Max(546, autoTransferSettings.MinimumBalanceThreshold); // Ensure that threshold is at least dust amount
        decimal availableBalance = walletBalance;

        foreach (var destination in autoPayoutSettings.Destinations)
        {
            if (destination.Amount < autoPayoutSettings.SatThreshold) continue;

            var percentage = autoPayoutSettings.ReserveFeePercentage / 100m;
            var reserveFee = (long)Math.Max(0, Math.Round(destination.Amount * percentage, 0, MidpointRounding.AwayFromZero));
            var payoutAmount = destination.Amount - reserveFee;
            if (payoutAmount <= 0) continue;

            if (availableBalance - payoutAmount < thresholdBalance) continue;

            var destinationStore = await _storeRepo.FindStore(destination.StoreId);
            if (destinationStore == null) continue;

            var payout = await ProcessOnchainClaimRequest(destinationStore.Id, storeId, payoutAmount);
            if (payout == null) continue;

            autoTransferSettings.PendingPayouts ??= new Dictionary<string, AutoTransferPayout>();
            autoTransferSettings.PendingPayouts.Add(payout.Id, new AutoTransferPayout(payoutAmount, reserveFee, destination.StoreId, destinationStore.StoreName, DateTimeOffset.Now));
            await UpdateAutoTransferSettingsForStore(storeId, autoTransferSettings);
            availableBalance -= payoutAmount;
        }
        return (true, "Payouts processed successfully");
    }

    public async Task<PayoutData> ProcessOnchainClaimRequest(string destinationStoreId, string sourceStoreId, long amountInSat)
    {
        WalletId walletId = new WalletId(destinationStoreId, _network);
        var address = (await _walletReceiveService.GetOrGenerate(walletId)).Address;
        string destination = address?.ToString();
        if (string.IsNullOrEmpty(destination)) return null;

        var claimRequest = new ClaimRequest()
        {
            Destination = new PrismPlaceholderClaimDestination(destination),
            PreApprove = true,
            StoreId = sourceStoreId,
            PayoutMethodId = PayoutTypes.CHAIN.GetPayoutMethodId(_network),
            ClaimedAmount = Money.Satoshis(amountInSat).ToDecimal(MoneyUnit.BTC),
            Metadata = JObject.FromObject(new
            {
                Source = "Auto payout"
            })
        };
        claimRequest = (await _pluginHookService.ApplyFilter("prism-claim-create", claimRequest)) as ClaimRequest;
        if (claimRequest is null) return null;

        var claimResponse = await _pullPaymentHostedService.Claim(claimRequest);
        if (claimResponse.Result != ClaimRequest.ClaimResult.Ok) return null;
        return claimResponse.PayoutData;
    }

    public async Task<decimal> GetStoreWalletBalance(StoreData store)
    {
        var network = GetNetwork();
        WalletId walletId = new WalletId(store.Id, _network);
        var paymentMethod = store.GetDerivationSchemeSettings(_handlers, walletId.CryptoCode);
        if (paymentMethod == null || store is null)
            return 0;

        var balance = _walletProvider.GetWallet(network).GetBalance(paymentMethod.AccountDerivation);
        var Balance = await balance;
        var availableBalance = (Balance.Available ?? Balance.Total).GetValue(network);
        return availableBalance * 100_000_000m;
    }

    private BTCPayNetwork GetNetwork()
    {
        if (!_handlers.TryGetValue(PaymentTypes.LNURL.GetPaymentMethodId(_network), out var o) ||
            o is not LNURLPayPaymentHandler { Network: var network })
            return null;
        return network;
    }

    public class AutoTransferPaymentEvent
    {
        public string StoreId { get; set; }
        public AutoTransferSettings Settings { get; set; }
    }
}