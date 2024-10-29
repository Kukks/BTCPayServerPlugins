#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Bcpg.OpenPgp;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;
using PayoutData = BTCPayServer.Data.PayoutData;

namespace BTCPayServer.Plugins.Bringin;

public static class StringExtensions
{

    public static string ToHumanReadable(this string str)
    {
        if(string.IsNullOrEmpty(str))
            return string.Empty;
        return  string.Join(' ', str.Split('_', '-').Select(part => 
            CultureInfo.CurrentCulture.TextInfo.ToTitleCase(part.ToLower(CultureInfo.CurrentCulture))));
    }
}
public class BringinService : EventHostedServiceBase
{
    private readonly ILogger<BringinService> _logger;
    private readonly StoreRepository _storeRepository;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private ConcurrentDictionary<string, BringinStoreSettings> _settings;
    private readonly AsyncKeyedLocker<string> _storeLocker = new();

    public BringinService(EventAggregator eventAggregator, 
        ILogger<BringinService> logger,
        StoreRepository storeRepository,
        PullPaymentHostedService pullPaymentHostedService,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
        IHttpClientFactory httpClientFactory, BTCPayNetworkProvider btcPayNetworkProvider) : base(eventAggregator, logger)
    {
        _logger = logger;
        _storeRepository = storeRepository;
        _pullPaymentHostedService = pullPaymentHostedService;
        _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
        _httpClientFactory = httpClientFactory;
        _btcPayNetworkProvider = btcPayNetworkProvider;
    }

    protected override void SubscribeToEvents()
    {
        base.SubscribeToEvents();
        Subscribe<StoreRemovedEvent>();
        Subscribe<InvoiceEvent>();
        Subscribe<PayoutEvent>();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = new ConcurrentDictionary<string, BringinStoreSettings>(
            await _storeRepository.GetSettingsAsync<BringinStoreSettings>(BringinStoreSettings.BringinSettings));
        await CheckPendingPayouts();
        _ = PeriodicallyCheckEditModes();
        await base.StartAsync(cancellationToken);
    }


    private async Task PeriodicallyCheckEditModes()
    {
        while (!CancellationToken.IsCancellationRequested)
        {
            foreach (var (storeId, (disposable, _, expiry)) in _editModes)
            {
                if (expiry < DateTimeOffset.Now)
                {
                    await CancelEdit(storeId);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }


    private async Task HandleStoreAction(string storeId, Func<BringinStoreSettings, Task> action)
    {
        using (await _storeLocker.LockAsync(storeId))
        {
            if (_settings.TryGetValue(storeId, out var bringinStoreSettings))
            {
                await action(bringinStoreSettings);
            }
        }
    }


    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        var storeId = evt switch
        {
            StoreRemovedEvent storeRemovedEvent => storeRemovedEvent.StoreId,
            InvoiceEvent invoiceEvent => invoiceEvent.Invoice.StoreId,
            PayoutEvent payoutEvent => payoutEvent.Payout.StoreDataId,
            _ => null
        };

        if (storeId is not null)
        {
            _ = HandleStoreAction(storeId, async bringinStoreSettings =>
            {
                switch (evt)
                {
                    case StoreRemovedEvent storeRemovedEvent:
                        _settings.TryRemove(storeRemovedEvent.StoreId, out _);
                        break;
                    case InvoiceEvent
                        {
                            EventCode:
                            InvoiceEventCode.Completed or InvoiceEventCode.MarkedCompleted
                        }
                        invoiceEvent when bringinStoreSettings.Enabled:

                        var pmPayments = invoiceEvent.Invoice.GetPayments("BTC", true)
                            .GroupBy(payment => payment.PaymentMethodId);
                        var update = false;
                        foreach (var pmPayment in pmPayments)
                        {
                            var methodId = pmPayment.Key;
                            if (methodId == PaymentTypes.LNURL.GetPaymentMethodId("BTC"))
                            {
                                methodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
                            }
                            if (!bringinStoreSettings.MethodSettings.TryGetValue(methodId.ToString(),
                                    out var methodSettings))
                            {
                                continue;
                            }

                            methodSettings.CurrentBalance +=
                                pmPayment.Sum(payment => payment.Value);
                            update = true;
                        }

                        if (update)
                        {
                            await _storeRepository.UpdateSetting(invoiceEvent.Invoice.StoreId,
                                BringinStoreSettings.BringinSettings, bringinStoreSettings);
                            await CheckIfNewPayoutsNeeded(invoiceEvent.Invoice.StoreId, bringinStoreSettings);
                        }

                        break;
                    case PayoutEvent payoutEvent:

                        if (HandlePayoutState(payoutEvent.Payout))
                        {
                            await CheckIfNewPayoutsNeeded(payoutEvent.Payout.StoreDataId, bringinStoreSettings);
                        }

                        break;
                }
            });
        }

        await base.ProcessEvent(evt, cancellationToken);
    }

    private async Task<bool> CheckIfNewPayoutsNeeded(string storeId, BringinStoreSettings bringinStoreSetting)
    {
        if (!bringinStoreSetting.Enabled)
            return false;
        var result = false;
        // check if there are any payouts that need to be created by looking at the balance and threshold
        // for onchain, we may also try and cancel a payout if there is a pending balance so that we dont needlessly create multiple transactions

        foreach (var methodSetting in bringinStoreSetting.MethodSettings)
        {
            var pmi = PayoutMethodId.TryParse(methodSetting.Key);
            if (pmi is null)
            {
                continue;
            }
            var isOnChain = PayoutTypes.CHAIN.GetPayoutMethodId("BTC") == pmi;
            // if there is a pending payout, try and cancel it if this is onchain as we want to save needless tx fees
            if (methodSetting.Value.PendingPayouts.Count > 0 && isOnChain)
            {
                var cancelResult = await _pullPaymentHostedService.Cancel(
                    new PullPaymentHostedService.CancelRequest(methodSetting.Value.PendingPayouts.ToArray(),
                        new[] {storeId}));

                if (cancelResult.Values.Any(value => value == MarkPayoutRequest.PayoutPaidResult.Ok))
                {
                    continue;
                }
            }


            try
            {
                
                var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
                var bringinClient = bringinStoreSetting.CreateClient(_httpClientFactory,network.NBitcoinNetwork);

                var thresholdAmount = methodSetting.Value.Threshold;
                if (methodSetting.Value.FiatThreshold)
                {
                    var rate = await bringinClient.GetRate();
                    thresholdAmount = methodSetting.Value.Threshold / rate.BringinPrice;
                }

                if (methodSetting.Value.CurrentBalance >= thresholdAmount)
                {
                    var payoutId = await CreateOrder(storeId, pmi, Money.Coins(methodSetting.Value.CurrentBalance)
                        , true);
                    if (payoutId is not null)
                    {
                        methodSetting.Value.CurrentBalance -= methodSetting.Value.CurrentBalance;
                        methodSetting.Value.PendingPayouts.Add(payoutId);
                        result = true;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not create payout");
            }            
            
        }

        if (result)
        {
            await _storeRepository.UpdateSetting(storeId, BringinStoreSettings.BringinSettings,
                bringinStoreSetting);
            _settings.AddOrReplace(storeId, bringinStoreSetting);
        }

        return result;
    }
    
    public async Task<string?> CreateOrder(string storeId, PayoutMethodId paymentMethodId, Money amountBtc, bool payout)
    {
        if (SupportedMethods.All(supportedMethod => supportedMethod.PayoutMethod != paymentMethodId))
        {
            throw new NotSupportedException($"{paymentMethodId} Payment method not supported");
           
        }
        var settings = _settings[storeId];
        
        var bringinClient = settings.CreateClient(_httpClientFactory, _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC").NBitcoinNetwork);
        
        
        var host = await Dns.GetHostEntryAsync(Dns.GetHostName(), CancellationToken.None);
        var ipToUse = host.AddressList
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString();

        
        
        var supportedMethod = SupportedMethods.First(supportedMethod => supportedMethod.PayoutMethod == paymentMethodId);
        //check if amount is enough
        if (supportedMethod.FiatMinimumAmount > 0)
        {
            
            var rate = await bringinClient.GetRate();
            var thresholdAmount = supportedMethod.FiatMinimumAmount  / rate.BringinPrice;
            if (amountBtc.ToDecimal(MoneyUnit.BTC) <= thresholdAmount)
            {
                throw new Exception($"Amount is too low. Minimum amount is {Money.Coins(thresholdAmount)} BTC");
            }
         
        }
           
        var request = new BringinClient.CreateOrderRequest()
        {
            SourceAmount = amountBtc.Satoshi,
            IP = ipToUse,
            PaymentMethod = supportedMethod.bringinMethod
        };
        var order = await bringinClient.PlaceOrder(request);
        var orderMoney = Money.Satoshis(order.Amount);

        if (!payout)
        {
            return order.Invoice?? order.DepositAddress;
        }
        var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
        
        var destination = !string.IsNullOrEmpty(order.Invoice)?  (IClaimDestination) new BoltInvoiceClaimDestination(order.Invoice, BOLT11PaymentRequest.Parse(order.Invoice, network.NBitcoinNetwork)):
            new AddressClaimDestination(BitcoinAddress.Create(order.DepositAddress, network.NBitcoinNetwork));
        var claim = await _pullPaymentHostedService.Claim(new ClaimRequest()
        {
            PayoutMethodId = paymentMethodId,
            StoreId = storeId,
            Destination = destination,
            ClaimedAmount = orderMoney.ToUnit(MoneyUnit.BTC),
            PreApprove = true,
            Metadata = JObject.FromObject(new
            {
                Source = "Bringin"
            })
        });
        if (claim.Result != ClaimRequest.ClaimResult.Ok)
        {
            throw new Exception($"Could not create payout because {ClaimRequest.GetErrorMessage(claim.Result)}");
        }
        return claim?.PayoutData?.Id;
        
        
    }

    public bool IsInEditMode(string storeId)
    {
        return _editModes.ContainsKey(storeId);
    }
    
    private async Task CheckPendingPayouts()
    {
        var payoutsIdsToCheck = _settings.SelectMany(pair =>
            pair.Value.MethodSettings.Values.SelectMany(settings => settings.PendingPayouts));
        var payouts = await _pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
        {
            PayoutIds = payoutsIdsToCheck.ToArray()
        });
        var storesToUpdate = new HashSet<string>();
        foreach (var payout in payouts.Where(HandlePayoutState))
        {
            storesToUpdate.Add(payout.StoreDataId);
        }

        foreach (var (storeId, bringinStoreSettings) in _settings)
        {
            if (await CheckIfNewPayoutsNeeded(storeId, bringinStoreSettings))
            {
                //the method updates so no need to do it again
                storesToUpdate.Remove(storeId);
            }
        }


        foreach (var storeId in storesToUpdate)
        {
            await _storeRepository.UpdateSetting(storeId, BringinStoreSettings.BringinSettings, _settings[storeId]);
        }
    }

    private bool HandlePayoutState(PayoutData payout)
    {
        switch (payout.State)
        {
            case PayoutState.Completed:
                // remove from pending payouts list in a setting
                return _settings[payout.StoreDataId].MethodSettings[payout.GetPayoutMethodId().ToString()]
                    .PendingPayouts.Remove(payout.Id);
            case PayoutState.Cancelled:
                // remove from pending payouts list in a setting and add to a balance
                var result = _settings[payout.StoreDataId].MethodSettings[payout.GetPayoutMethodId().ToString()]
                    .PendingPayouts.Remove(payout.Id);
                var pmi = payout.GetPayoutMethodId();
                if (_settings[payout.StoreDataId].MethodSettings
                    .TryGetValue(pmi.ToString(), out var methodSettings))
                {
                    methodSettings.CurrentBalance += payout.Amount ?? payout.OriginalAmount;
                    result = true;
                }

                return result;
        }

        return false;
    }


    public async Task<BringinStoreSettings?> Get(string storeId)
    {
        return _settings.TryGetValue(storeId, out var bringinStoreSettings) ? bringinStoreSettings : null;
    }

    public record SupportedMethodOptions(PayoutMethodId PayoutMethod, bool FiatMinimum, decimal FiatMinimumAmount, string bringinMethod);

    public static readonly SupportedMethodOptions[] SupportedMethods = new[]
    {
        new SupportedMethodOptions(PayoutTypes.LN.GetPayoutMethodId("BTC"), true, 22, "LIGHTNING"),
        new SupportedMethodOptions(PayoutTypes.CHAIN.GetPayoutMethodId("BTC"), true, 22, "ON_CHAIN"),
    };

    private ConcurrentDictionary<string, (IDisposable, BringinStoreSettings, DateTimeOffset Expiry)> _editModes = new();

    public async Task<BringinStoreSettings> Update(string storeId)
    {
        var isNew = false;
        var result = _editModes.GetOrAdd(storeId, (s) =>
        {
            var storeLock = _storeLocker.Lock(s);
            var result = (_settings.TryGetValue(s, out var bringinStoreSettings)
                ? JObject.FromObject(bringinStoreSettings).ToObject<BringinStoreSettings>()
                : new BringinStoreSettings())!;

            // add or remove any missing methods in result
            foreach (var supportedMethod in SupportedMethods)
            {
                if (!result.MethodSettings.ContainsKey(supportedMethod.PayoutMethod.ToString()))
                {
                    result.MethodSettings.Add(supportedMethod.PayoutMethod.ToString(),
                        new BringinStoreSettings.PaymentMethodSettings()
                        {
                            FiatThreshold = supportedMethod.FiatMinimum,
                            Threshold = supportedMethod.FiatMinimum ? supportedMethod.FiatMinimumAmount : 0.1m
                        });
                }
            }

            result.MethodSettings = result.MethodSettings.Where(pair =>
                    SupportedMethods.Any(supportedMethod => supportedMethod.PayoutMethod.ToString() == pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            isNew = true;
            return (storeLock, result, DateTimeOffset.Now.AddMinutes(5));
        });
        result.Expiry = DateTimeOffset.Now.AddMinutes(5);
        if (_storeLocker.IsInUse(storeId))
        {
            if (isNew)
                EditModeChanged?.Invoke(this, (storeId, true));
            return result.Item2;
        }

        _editModes.Remove(storeId, out _);
        return await Update(storeId);
    }

    public EventHandler<(string storeId, bool editMode)> EditModeChanged;

    public async Task Update(string storeId, BringinStoreSettings bringinStoreSettings)
    {
        if (!_editModes.Remove(storeId, out var editModeLock))
            return;
        editModeLock.Item1.Dispose();

        await _storeRepository.UpdateSetting(storeId, BringinStoreSettings.BringinSettings, bringinStoreSettings);
        _settings.AddOrReplace(storeId, bringinStoreSettings);

        EditModeChanged?.Invoke(this, (storeId, false));
    }

    public async Task<bool> CancelEdit(string storeId)
    {
        if (!_editModes.Remove(storeId, out var editModeLock))
            return false;
        editModeLock.Item1.Dispose();
        EditModeChanged?.Invoke(this, (storeId, false));
        return true;
    }

    public class BringinStoreSettings
    {
        public const string BringinSettings = "BringinSettings";
        public bool Enabled { get; set; } = true;
        public string ApiKey { get; set; }
        public string Code { get; set; } = Guid.NewGuid().ToString();
        public Dictionary<string, PaymentMethodSettings> MethodSettings { get; set; } = new();

        public class PaymentMethodSettings
        {
            public decimal Threshold { get; set; }
            public bool FiatThreshold { get; set; }
            public decimal PercentageToForward { get; set; } = 99;
            public decimal CurrentBalance { get; set; } = 0m;
            public List<string> PendingPayouts { get; set; } = new();
        }

        public BringinClient CreateClient(IHttpClientFactory httpClientFactory, Network network)
        {
            var httpClient = BringinClient.CreateClient(network, httpClientFactory, ApiKey);
            return new BringinClient(ApiKey, httpClient);
        }
    }

    public async Task ResetBalance(string storeId, PaymentMethodId pmi)
    {
        await HandleStoreAction(storeId, async bringinStoreSettings =>
        {
            if (bringinStoreSettings.MethodSettings.TryGetValue(pmi.ToString(), out var methodSettings) && methodSettings.CurrentBalance > 0)
            {
                methodSettings.CurrentBalance = 0;
                await _storeRepository.UpdateSetting(storeId, BringinStoreSettings.BringinSettings, bringinStoreSettings);
                _settings.AddOrReplace(storeId, bringinStoreSettings);
            }
        });
    }
}