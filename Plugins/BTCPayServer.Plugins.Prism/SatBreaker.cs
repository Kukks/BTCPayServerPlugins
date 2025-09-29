using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Prism
{
    internal class PrismPlaceholderClaimDestination:IClaimDestination
    {
        public PrismPlaceholderClaimDestination(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public decimal? Amount { get; } = null;
        
        public override string ToString()
        {
            return Id;
        }
    }
    
    /// <summary>
    /// monitors stores that have prism enabled and detects incoming payments based on the lightning address splits the funds to the destinations once the threshold is reached
    /// </summary>
    public class SatBreaker : EventHostedServiceBase, IPeriodicTask
    {
        private readonly StoreRepository _storeRepository;
        private readonly ILogger<SatBreaker> _logger;
        private readonly PullPaymentHostedService _pullPaymentHostedService;
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly LightningClientFactoryService _lightningClientFactoryService;
        private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
        private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
        private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
        private readonly PayoutMethodHandlerDictionary _payoutMethodHandlerDictionary;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IPluginHookService _pluginHookService;
        private readonly BTCPayWalletProvider _walletProvider;

        private Dictionary<string, PrismSettings> _prismSettings;

        public event EventHandler<PrismPaymentDetectedEventArgs> PrismUpdated;

        public SatBreaker(StoreRepository storeRepository,
            EventAggregator eventAggregator,
            ILogger<SatBreaker> logger,
            PullPaymentHostedService pullPaymentHostedService,
            BTCPayNetworkProvider btcPayNetworkProvider,
            LightningClientFactoryService lightningClientFactoryService,
            IOptions<LightningNetworkOptions> lightningNetworkOptions,
            BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
            PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
            PayoutMethodHandlerDictionary payoutMethodHandlerDictionary,
            ISettingsRepository settingsRepository,
            IPluginHookService pluginHookService,
            BTCPayWalletProvider walletProvider
            ) : base(eventAggregator, logger)
        {
            _storeRepository = storeRepository;
            _logger = logger;
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _lightningNetworkOptions = lightningNetworkOptions;
            _pullPaymentHostedService = pullPaymentHostedService;
            _lightningClientFactoryService = lightningClientFactoryService;
            _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
            _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
            _payoutMethodHandlerDictionary = payoutMethodHandlerDictionary;
            _settingsRepository = settingsRepository;
            _pluginHookService = pluginHookService;
            _walletProvider = walletProvider;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _prismSettings = await _storeRepository.GetSettingsAsync<PrismSettings>(nameof(PrismSettings));
            foreach (var keyValuePair in _prismSettings)
            {
                keyValuePair.Value.Splits ??= new List<Split>();
                keyValuePair.Value.Destinations ??= new Dictionary<string, PrismDestination>();
                keyValuePair.Value.PendingPayouts ??= new Dictionary<string, PendingPayout>();
            }

            await base.StartAsync(cancellationToken);
            PushEvent(new CheckPayoutsEvt());
        }

        public record GlobalPrismSettings
        {
            public DateTimeOffset? LastProcessedDate  { get; set; }
        }
        public record ScheduleDayEvent(string forceSplitSource = null);

        public async Task Do(CancellationToken cancellationToken)
        {

            var globalPrismSettings = await _settingsRepository.GetSettingAsync<GlobalPrismSettings>()??new GlobalPrismSettings();

            if (globalPrismSettings.LastProcessedDate.HasValue && globalPrismSettings.LastProcessedDate.Value.Day == DateTimeOffset.UtcNow.Day)
            {
                return;
            } 
            PushEvent(new ScheduleDayEvent());
            
            
            globalPrismSettings.LastProcessedDate = DateTimeOffset.UtcNow;
            await _settingsRepository.UpdateSetting(globalPrismSettings);
            
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();
            Subscribe<InvoiceEvent>();
            Subscribe<PayoutEvent>();
            Subscribe<ScheduleDayEvent>();
            Subscribe<StoreEvent.Removed>();
        }

        class CheckPayoutsEvt
        {
        }

        private TaskCompletionSource _checkPayoutTcs = new();
        
        private async Task<ILightningClient> ConstructLightningClient(string storeId)
        {
            var store = await _storeRepository.FindStore(storeId);

            var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
            var id = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var existing =
                store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(id,
                    _paymentMethodHandlerDictionary);
            if (existing?.GetExternalLightningUrl() is { } connectionString)
            {
                return _lightningClientFactoryService.Create(connectionString,
                    network);
            }
            else if (existing?.IsInternalNode is true &&
                     _lightningNetworkOptions.Value.InternalLightningByCryptoCode
                         .TryGetValue(network.CryptoCode,
                             out var internalLightningNode))
            {
                return internalLightningNode;
            }
            return null;
   
        }
        

        /// <summary>
        /// Go through generated payouts and check if they are completed or cancelled, and then remove them from the list.
        /// If the fee can be fetched, we compute what the difference was from the original fee we computed (hardcoded 2% of the balance)
        /// and we adjust the balance with the difference( credit if the fee was lower, debit if the fee was higher)
        /// </summary>
        /// <param name="cancellationToken"></param>
        private async Task CheckPayouts(CancellationToken cancellationToken)
        {
            try
            {
                _checkPayoutTcs = new TaskCompletionSource();

                var payoutsToCheck =
                    _prismSettings.ToDictionary(pair => pair.Key, pair => pair.Value.PendingPayouts);
                var payoutIds = payoutsToCheck
                    .SelectMany(pair => pair.Value?.Keys.ToArray() ?? Array.Empty<string>()).ToArray();
                var payouts = (await _pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
                {
                    PayoutIds = payoutIds,
                    States = new[] {PayoutState.Cancelled, PayoutState.Completed}
                }));
                var lnClients = new Dictionary<string, ILightningClient>();
                var payoutsByStore = payouts.GroupBy(data => data.StoreDataId);
                foreach (var storePayouts in payoutsByStore)
                {
                    if (!_prismSettings.TryGetValue(storePayouts.Key, out var prismSettings))
                    {
                        continue;
                    }

                    if (!payoutsToCheck.TryGetValue(storePayouts.Key, out var pendingPayouts))
                    {
                        continue;
                    }
                    
                    foreach (var payout in storePayouts)
                    {
                        
                        if (!pendingPayouts.TryGetValue(payout.Id, out var pendingPayout))
                        {
                            continue;
                        }
                        
                        if(payout.GetPayoutMethodId() is not { } payoutMethodId)
                            continue;

                        if (!_payoutMethodHandlerDictionary.TryGetValue(payoutMethodId, out var handler))
                        {
                            continue;
                        }
                        long toCredit = 0;
                        switch (payout.State)
                        {
                            case PayoutState.Completed:
                                
                                var proof = handler.ParseProof(payout) as PayoutLightningBlob;

                                long? feePaid = null;
                                if (!string.IsNullOrEmpty(proof?.PaymentHash))
                                {
                                    if (!lnClients.TryGetValue(payout.StoreDataId, out var lnClient))
                                    {
                                        lnClient = await ConstructLightningClient(payout.StoreDataId);

                                        lnClients.Add(payout.StoreDataId, lnClient);
                                    }

                                    if (lnClient is not null && proof?.PaymentHash is not null)
                                    {
                                        try
                                        {
                                            var p = await lnClient.GetPayment(proof.PaymentHash, CancellationToken);
                                            feePaid = (long) p?.Fee?.ToUnit(LightMoneyUnit.Satoshi);
                                        }
                                        catch (Exception e)
                                        {
                                            _logger.LogError(e,
                                                "The payment fee could not be fetched from the lightning node due to an error, so we will use the allocated 2% as the fee.");
                                        }
                                    }
                                }

                                if (feePaid is not null)
                                {
                                    toCredit = pendingPayout.FeeCharged - feePaid.Value;
                                }

                                break;
                            case PayoutState.Cancelled:
                                toCredit = pendingPayout.PayoutAmount + pendingPayout.FeeCharged - pendingPayout.ScheduledTransferAmount;
                                break;
                        }

                        var destinationId = pendingPayout.DestinationId ??
                                            payout.GetBlob(_btcPayNetworkJsonSerializerSettings).Destination;
                        if (prismSettings.DestinationBalance.TryGetValue(destinationId,
                                out var currentBalance))
                        {
                            prismSettings.DestinationBalance[destinationId] =
                                currentBalance + (toCredit * 1000);
                        }
                        else
                        {
                            prismSettings.DestinationBalance.Add(destinationId,
                                (toCredit * 1000));
                        }

                        prismSettings.PendingPayouts.Remove(payout.Id);
                    }

                    if (await CreatePayouts(storePayouts.Key, prismSettings, new()))
                    {
                        await UpdatePrismSettingsForStore(storePayouts.Key, prismSettings, true);
                    }
                }

            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while checking payouts");
            }

            _ = Task.WhenAny(_checkPayoutTcs.Task, Task.Delay(TimeSpan.FromMinutes(1), cancellationToken)).ContinueWith(
                task =>
                {
                    if (!task.IsCanceled)
                        PushEvent(new CheckPayoutsEvt());
                }, cancellationToken);
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

        public async Task<PrismSettings> Get(string storeId)
        {
            return JObject
                .FromObject(_prismSettings.TryGetValue(storeId, out var settings) && settings is not null
                    ? settings
                    : new PrismSettings()).ToObject<PrismSettings>();
        }


        public async Task<bool> UpdatePrismSettingsForStore(string storeId, PrismSettings updatedSettings,
            bool skipLock = false)
        {
            try
            {
                if (!skipLock)
                    await WaitAndLock();
                var currentSettings = await Get(storeId);

                if (currentSettings.Version != updatedSettings.Version)
                {
                    return false; // Indicate that the update failed due to a version mismatch
                }

                updatedSettings.Version++; // Increment the version number

                // Update the settings in the dictionary
                _prismSettings.AddOrReplace(storeId, updatedSettings);

                // Update the settings in the StoreRepository
                await _storeRepository.UpdateSetting(storeId, nameof(PrismSettings), updatedSettings);
            }

            finally
            {
                if (!skipLock)
                    Unlock();
            }

            var prismPaymentDetectedEventArgs = new PrismPaymentDetectedEventArgs()
            {
                StoreId = storeId,
                Settings = updatedSettings
            };
            PrismUpdated?.Invoke(this, prismPaymentDetectedEventArgs);
            return true; // Indicate that the update succeeded
        }


        private (Split, LightMoney)[] DetermineMatches(PrismSettings prismSettings, InvoiceEntity entity)
        {
            //first check the primary thing - ln address
            var explicitPMI = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
            var pm = entity.GetPaymentPrompt(explicitPMI);
            
            var payments = entity.GetPayments(true).GroupBy(paymentEntity => paymentEntity.PaymentMethodId).ToArray();
            List<(Split, LightMoney)> result = new();
            if(_paymentMethodHandlerDictionary.TryGetValue(explicitPMI, out var handler) && pm is not null)
            {
                var pmd = handler.ParsePaymentPromptDetails(pm.Details) as LNURLPayPaymentMethodDetails;
                
            
                if (pmd?.ConsumedLightningAddress is not null)
                {
                    var address = pmd.ConsumedLightningAddress.Split("@")[0];
                    var matchedExplicit = prismSettings.Splits.FirstOrDefault(s =>
                        s.Source.Equals(address, StringComparison.InvariantCultureIgnoreCase));

                    if (matchedExplicit is not null)
                    {
                        var explicitPayments = payments.FirstOrDefault(grouping =>
                            grouping.Key == explicitPMI)?.Sum(paymentEntity => paymentEntity.PaidAmount.Net);
                        payments = payments.Where(grouping => grouping.Key != explicitPMI).ToArray();

                        if (explicitPayments > 0)
                        {
                            result.Add((matchedExplicit, LightMoney.FromUnit(explicitPayments.Value, LightMoneyUnit.BTC)));
                        }
                    }
                } 
            }
            
            var catchAlls = prismSettings.Splits.Where(split => split.Source.StartsWith("*")).Select(split =>
            {
                PaymentMethodId pmi = null;
                var valid = true;

                switch (split.Source)
                {
                    case "*":
                        pmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
                        break;
                    case "*All":
                        break;
                    case var s when s.StartsWith("*") && s.Substring(1) ==PaymentTypes.CHAIN.ToString():
                        pmi = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
                        break;
                    case var s2 when s2.StartsWith("*") && s2.Substring(1) ==PaymentTypes.LN.ToString():
                        pmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
                        break;
                    case var s3 when s3.StartsWith("*") && s3.Substring(1) ==PaymentTypes.LNURL.ToString():
                        pmi = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
                        break;
                    case var s when !PaymentMethodId.TryParse(s.Substring(1), out pmi):
                        valid = false;
                        break;
                }

                if (pmi is not null && !pmi.ToString().StartsWith("BTC-"))
                {
                    valid = false;
                }

                return (pmi, valid, split);
            }).Where(tuple => tuple.valid).ToDictionary(split => split.pmi, split => split.split);


            var posSplits = prismSettings.Splits.Where(split => split.Source.StartsWith("pos", StringComparison.OrdinalIgnoreCase)).ToDictionary(s => s.Source, StringComparer.OrdinalIgnoreCase);
            var appItems = GetInvoiceAppIds(entity);
            if (posSplits.Count > 0 && appItems?.Count > 0)
            {
                foreach (var (posId, items) in appItems)
                {
                    if (items is null || !items.Any()) continue;
                    foreach (var item in items)
                    {
                        var sourceKey = $"pos:{posId}:{item.Id}";
                        if (!posSplits.TryGetValue(sourceKey, out var split) || split.Destinations.Count == 0) continue;

                        var itemTotal = item.Count * item.Price;
                        var payment = entity.GetPayments(true).FirstOrDefault(p => p.Status == PaymentStatus.Settled);
                        if (payment == null) continue;

                        var itemTotalInSats = entity.Currency switch
                        {
                            "BTC" => (long)(itemTotal * 100_000_000m),
                            "SATS" => (long)itemTotal,
                            _ => (long)((itemTotal / payment.Rate) * 100_000_000m)
                        };
                        result.Add((split, LightMoney.FromUnit(itemTotalInSats, LightMoneyUnit.Satoshi)));
                    }
                }
            }


            while (payments.Any() && catchAlls.Any())
            {
                decimal paymentSum;
                Split catchAllSplit;
                //check if all catachalls do not match to all payments.key and then check if there is a catch all with a null key, that will take all the payments
                if (catchAlls.All(catchAll => payments.All(payment => payment.Key != catchAll.Key)) && catchAlls.TryGetValue(null, out catchAllSplit))
                {

                    paymentSum = payments.Sum(paymentEntity =>
                        paymentEntity.Sum(paymentEntity => paymentEntity.PaidAmount.Net));

                    payments = Array.Empty<IGrouping<PaymentMethodId, PaymentEntity>>();
                }
                else
                {
                    var paymentGroup = payments.First();
                    if (!catchAlls.Remove(paymentGroup.Key, out catchAllSplit))
                    {
                        //shift the paymentgroup to bottom of the list
                        payments = payments.Where(grouping => grouping.Key != paymentGroup.Key).Append(paymentGroup).ToArray();
                        continue;
                    }

                    paymentSum = paymentGroup.Sum(paymentEntity => paymentEntity.PaidAmount.Net);
                    payments = payments.Where(grouping => grouping.Key != paymentGroup.Key).ToArray();
                }
                if (paymentSum > 0)
                {
                    result.Add((catchAllSplit, LightMoney.FromUnit(paymentSum, LightMoneyUnit.BTC)));
                }
            }

            return result.ToArray();
        }


        public static (int[] schedule, PaymentMethodId paymentMethod, LightMoney amount)? ParseSource(string source)
        {
            return TryParseSource(source, out var dest)? dest: null;
        }

        public static string EncodeSource(int[] schedule, PaymentMethodId paymentMethod, LightMoney amount )
        {
            return $"storetransfer:{paymentMethod}:{amount}:{string.Join(',', schedule)}";
        }
        
        public static bool TryParseSource(string source, out (int[] schedule, PaymentMethodId paymentMethod, LightMoney? amount)? dest)
        {;

            try
            {
                if (source.StartsWith("storetransfer:"))
                {
                    var parts = source.Split(':');

                    var schedule = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

                    var paymentMethod = string.IsNullOrEmpty(parts[1])? PaymentTypes.LN.GetPaymentMethodId("BTC"): PaymentMethodId.Parse(parts[1]);
                    
                    var amount = string.IsNullOrEmpty(parts[2])? null: LightMoney.Parse(parts[2]);

                    
                    dest = (schedule, paymentMethod, amount);
                    return true;
                }
            }
            catch (Exception)
            {

            }

            dest = null;

            return false;

        }
        

        /// <summary>
        /// if an invoice is completed, check if it was created through a lightning address, and if the store has prism enabled and one of the splits' source is the same lightning address, grab the paid amount, split it based on the destination percentages, and credit it inside the prism destbalances.
        /// When the threshold is reached (plus a 2% reserve fee to account for fees), create a payout and deduct the balance. 
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="cancellationToken"></param>
        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            try
            {
                await WaitAndLock(cancellationToken);

                switch (evt)
                {
                    case StoreEvent.Removed storeRemovedEvent:
                        _prismSettings.Remove(storeRemovedEvent.StoreId);
                        return;
                    case ScheduleDayEvent scheduleDayEvent:
                        foreach (var (storeId, prismSettings) in _prismSettings)
                        {
                                if (!prismSettings.EnableScheduledAutomation && string.IsNullOrEmpty(scheduleDayEvent.forceSplitSource)) continue;

                                var prisms = await DetermineMatches(storeId, prismSettings, DateTimeOffset.UtcNow.Day, scheduleDayEvent.forceSplitSource);
                                Dictionary<string,LightMoney> destinationAmounts = new();
                                foreach (var prism in prisms)
                                {
                                    if (prism.Item2 is not { } msats || msats <= 0)
                                        continue;
                                    var splits = prism.Item1?.Destinations;
                                    if (splits?.Any() is not true)
                                        continue;

                                    //compute the sats for each destination  based on splits percentage
                                    var msatsPerDestination =
                                        splits.ToDictionary(s => s.Destination,
                                            s => (long) (msats.MilliSatoshi * (s.Percentage / 100)));

                                    foreach (var (destination, splitMSats) in msatsPerDestination)
                                    {
                                        if (destinationAmounts.TryGetValue(destination,
                                                out var currentBalance))
                                        {
                                            destinationAmounts[destination] = currentBalance + splitMSats;
                                        }
                                        else if (splitMSats > 0)
                                        {
                                            destinationAmounts.Add(destination, splitMSats);
                                        }
                                    }
                                }


                            if (await CreatePayouts(storeId, prismSettings, destinationAmounts))
                            {
                                await UpdatePrismSettingsForStore(storeId, prismSettings, true);
                            }
                        }

                        break;

                    case InvoiceEvent invoiceEvent when
                        new[] {InvoiceEventCode.Confirmed, InvoiceEventCode.MarkedCompleted}.Contains(
                            invoiceEvent.EventCode):
                    {
                        if (_prismSettings.TryGetValue(invoiceEvent.Invoice.StoreId, out var prismSettings) && prismSettings.Enabled)
                        {   
                            var prisms = DetermineMatches(prismSettings, invoiceEvent.Invoice);
                            foreach (var prism in prisms)
                            {
                                if (prism.Item2 is not { } msats || msats <= 0)
                                    continue;
                                var splits = prism.Item1?.Destinations;
                                if (splits?.Any() is not true)
                                    continue;

                                //compute the sats for each destination  based on splits percentage
                                var msatsPerDestination =
                                    splits.ToDictionary(s => s.Destination, s => (long)(msats.MilliSatoshi * (s.Percentage / 100)));

                                prismSettings.DestinationBalance ??= new Dictionary<string, long>();
                                foreach (var (destination, splitMSats) in msatsPerDestination)
                                {
                                    if (prismSettings.DestinationBalance.TryGetValue(destination, out var currentBalance))
                                    {
                                        prismSettings.DestinationBalance[destination] = currentBalance + splitMSats;
                                    }
                                    else if (splitMSats > 0)
                                    {
                                        prismSettings.DestinationBalance.Add(destination, splitMSats);
                                    }
                                }
                            }

                            await UpdatePrismSettingsForStore(invoiceEvent.Invoice.StoreId, prismSettings, true);
                            if (await CreatePayouts(invoiceEvent.Invoice.StoreId, prismSettings, new()))
                            {
                                await UpdatePrismSettingsForStore(invoiceEvent.Invoice.StoreId, prismSettings, true);
                            }
                        }
                        break;
                    }
                    case CheckPayoutsEvt:
                        await CheckPayouts(cancellationToken);
                        break;
                    case PayoutEvent payoutEvent when !_prismSettings.TryGetValue(payoutEvent.Payout.StoreDataId, out var prismSettings) || payoutEvent.Type != PayoutEvent.PayoutEventType.Approved:
                        return;
                    case PayoutEvent payoutEvent:
                        _checkPayoutTcs?.TrySetResult();
                        break;
                }
            }
            catch (Exception e)
            {
                Logs.PayServer.LogWarning(e, "Error while processing prism event");
            }
            finally
            {
                Unlock();
            }
        }

        private async Task<(Split, LightMoney)[]> DetermineMatches(string storeId, PrismSettings prismSettings, int utcNowDay, string forceSplitSource = null)
        {

            if (prismSettings.PendingPayouts.Any(pair => pair.Value.ScheduledTransferAmount > 0))
            {
                // we skip as there are payouts with scheduled transfer amount > 0 and this may conflict and end up oversending.
                return Array.Empty<(Split, LightMoney)>();
            }
            
            var splitsWithStoreTransfer = prismSettings.Splits.Select((split => (split, ParseSource(split.Source)))).Where(pair => pair.Item2 != null).ToArray();
            
            var splitsWithScheduleForToday = splitsWithStoreTransfer
                .Where(part =>  part.Item2.Value.schedule.Any(day => day == utcNowDay)).ToList();


            if (!splitsWithScheduleForToday.Any() && string.IsNullOrEmpty(forceSplitSource)) return Array.Empty<(Split, LightMoney)>();

            var result = new List<(Split, LightMoney)>();
            var lnClient = new Lazy<Task<ILightningClient>>(async () => await ConstructLightningClient(storeId)); 
            var onchainBalance = new Lazy<Task<LightMoney>>(async () =>
            {
                var store = await _storeRepository.FindStore(storeId);
                if (store is null) return null;
                var settings = store.GetDerivationSchemeSettings(_paymentMethodHandlerDictionary, "BTC", true);
                if (settings is null) return null;
                var wallet = _walletProvider.GetWallet("BTC");
                var res = await wallet.GetBalance(settings.AccountDerivation);
                return new LightMoney(Money.Coins(res.Available.GetValue(wallet.Network)));
            });

            var splitsToProcess = splitsWithScheduleForToday.ToList();
            if (!string.IsNullOrEmpty(forceSplitSource) && TryParseSource(forceSplitSource, out var forcedSplitSourceParsed)  && prismSettings.Splits.FirstOrDefault(s => s.Source.Equals(forceSplitSource, StringComparison.OrdinalIgnoreCase)) is { } forcedSplit)
            {
                splitsToProcess.Add((forcedSplit, forcedSplitSourceParsed));
            }
            
            

            foreach (var split in splitsToProcess)
            {
               
                var walletBalance = LightMoney.Zero;
                
                switch (split.Item2.Value.paymentMethod)
                {
                    case var pmi when pmi == PaymentTypes.LN.GetPaymentMethodId("BTC"):
                        var client = await lnClient.Value;
                        
                        if (client is null) continue;
                        var balance = await client.GetBalance();
                        walletBalance = balance.OffchainBalance.Local;
                       
                        
                        
                        break;
                    case var pmi when pmi == PaymentTypes.CHAIN.GetPaymentMethodId("BTC"):
                      walletBalance = await onchainBalance.Value;
                        break;
                    default:
                        continue;
                }

                if (walletBalance <= Money.Zero) continue;

                walletBalance = Math.Min(walletBalance, split.Item2.Value.amount ?? walletBalance);
                

                if (walletBalance > Money.Zero)
                {
                    result.Add((split.split, walletBalance));
                }
            }
            return result.ToArray();
        }

        private Dictionary<string, List<AppCartItem>> GetInvoiceAppIds(InvoiceEntity entity)
        {
            var appIds = AppService.GetAppInternalTags(entity);
            if (!appIds.Any()) return null;

            List<AppCartItem> cartItems = null;
            if ((!string.IsNullOrEmpty(entity.Metadata.ItemCode) || AppService.TryParsePosCartItems(entity.Metadata.PosData, out cartItems)))
            {
                cartItems ??= new List<AppCartItem>();
                if (!string.IsNullOrEmpty(entity.Metadata.ItemCode) && !cartItems.Exists(cartItem => cartItem.Id == entity.Metadata.ItemCode))
                {
                    cartItems.Add(new AppCartItem()
                    {
                        Id = entity.Metadata.ItemCode,
                        Count = 1,
                        Price = entity.Price
                    });
                }
            }
            return appIds.ToDictionary(id => id, _ => cartItems);
        }


        private async Task<bool> CreatePayouts(string storeId, PrismSettings prismSettings, Dictionary<string,LightMoney> destinationAmounts)
        {
            if (!prismSettings.Enabled)
            {
                return false;
            }
            var result = false;
            prismSettings.DestinationBalance ??= new Dictionary<string, long>();
            prismSettings.Destinations ??= new Dictionary<string, PrismDestination>();
            
            var copiedBalances = prismSettings.DestinationBalance.ToDictionary();
            foreach (var destinationAmount in destinationAmounts)
            {
                copiedBalances.TryAdd(destinationAmount.Key, 0);
            }
            
            foreach (var (destination, amtMsats) in copiedBalances)
            {
                prismSettings.Destinations.TryGetValue(destination, out var destinationSettings);
                
                var additionalAmount = destinationAmounts.TryGetValue(destination, out var additionalAmountMsats) ? additionalAmountMsats : LightMoney.Zero;

                var satThreshold = destinationSettings?.SatThreshold ?? prismSettings.SatThreshold;
                var reserve = destinationSettings?.Reserve ?? prismSettings.Reserve;

                var amt = (amtMsats + additionalAmount).MilliSatoshi / 1000;
                if (amt < satThreshold) continue;
                var percentage = reserve / 100;
                var reserveFee = (long) Math.Max(0, Math.Round(amt * percentage, 0, MidpointRounding.AwayFromZero));
                var payoutAmount = amt - reserveFee;
                if (payoutAmount <= 0)
                {
                    continue;
                }

                var pmi = string.IsNullOrEmpty(destinationSettings?.PayoutMethodId) ||
                          !PayoutMethodId.TryParse(destinationSettings?.PayoutMethodId, out var pmi2)
                    ? PayoutTypes.LN.GetPayoutMethodId("BTC")
                    : pmi2;

                var source = "Prism";
                if (destinationSettings is not null)
                {
                    source+= $" ({destination})";
                }
                var claimRequest = new ClaimRequest()
                {
                    Destination = new PrismPlaceholderClaimDestination(destinationSettings?.Destination ?? destination),
                    PreApprove = true,
                    StoreId = storeId,
                    PayoutMethodId = pmi,
                    ClaimedAmount = Money.Satoshis(payoutAmount).ToDecimal(MoneyUnit.BTC),
                    Metadata = JObject.FromObject(new
                    {
                        Source = source 
                    })
                };
                claimRequest =
                    (await _pluginHookService.ApplyFilter("prism-claim-create", claimRequest)) as ClaimRequest;

                if (claimRequest is null)
                {
                    continue;
                }

                var payout = await _pullPaymentHostedService.Claim(claimRequest);
                if (payout.Result != ClaimRequest.ClaimResult.Ok) continue;
                prismSettings.PendingPayouts ??= new Dictionary<string, PendingPayout>();
                long additionalSats = additionalAmount.MilliSatoshi / 1000;
                prismSettings.PendingPayouts.Add(payout.PayoutData.Id,
                    new PendingPayout(payoutAmount, reserveFee, destination, additionalSats));
                var newAmount = amtMsats - (payoutAmount + reserveFee) * 1000;
                if (newAmount <= 0)
                    prismSettings.DestinationBalance.Remove(destination);
                else
                {
                    prismSettings.DestinationBalance.AddOrReplace(destination, newAmount);
                }

                result = true;
            }

            return result;
        }

        public void TriggerPayoutCheck()
        {
            _checkPayoutTcs?.TrySetResult();
        }
    }

    public class PrismPaymentDetectedEventArgs
    {
        public string StoreId { get; set; }
        public PrismSettings Settings { get; set; }
    }
}