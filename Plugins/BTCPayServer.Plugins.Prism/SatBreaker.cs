using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.Prism
{
    /// <summary>
    /// monitors stores that have prism enabled and detects incoming payments based on the lightning address splits the funds to the destinations once the threshold is reached
    /// </summary>
    public class SatBreaker : EventHostedServiceBase
    {
        private readonly StoreRepository _storeRepository;
        private readonly ILogger<SatBreaker> _logger;
        private readonly LightningAddressService _lightningAddressService;
        private readonly PullPaymentHostedService _pullPaymentHostedService;
        private readonly LightningLikePayoutHandler _lightningLikePayoutHandler;
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly LightningClientFactoryService _lightningClientFactoryService;
        private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
        private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
        private Dictionary<string, PrismSettings> _prismSettings;

        public SatBreaker(StoreRepository storeRepository,
            EventAggregator eventAggregator,
            ILogger<SatBreaker> logger,
            LightningAddressService lightningAddressService,
            PullPaymentHostedService pullPaymentHostedService,
            LightningLikePayoutHandler lightningLikePayoutHandler,
            BTCPayNetworkProvider btcPayNetworkProvider,
            LightningClientFactoryService lightningClientFactoryService,
            IOptions<LightningNetworkOptions> lightningNetworkOptions,
            BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings) : base(eventAggregator, logger)
        {
            _storeRepository = storeRepository;
            _logger = logger;
            _lightningAddressService = lightningAddressService;
            _pullPaymentHostedService = pullPaymentHostedService;
            _lightningLikePayoutHandler = lightningLikePayoutHandler;
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _lightningClientFactoryService = lightningClientFactoryService;
            _lightningNetworkOptions = lightningNetworkOptions;
            _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _prismSettings = await _storeRepository.GetSettingsAsync<PrismSettings>(nameof(PrismSettings));
            await base.StartAsync(cancellationToken);
            _ = CheckPayouts(CancellationToken);
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();
            Subscribe<InvoiceEvent>();
        }

        /// <summary>
        /// Go through generated payouts and check if they are completed or cancelled, and then remove them from the list.
        /// If the fee can be fetched, we compute what the difference was from the original fee we computed (hardcoded 2% of the balance)
        /// and we adjust the balance with the difference( credit if the fee was lower, debit if the fee was higher)
        /// </summary>
        /// <param name="cancellationToken"></param>
        private async Task CheckPayouts(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
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
                    var res = new Dictionary<string, CreditDestination>();

                    foreach (var payout in payouts)
                    {
                        if (payoutsToCheck.TryGetValue(payout.StoreDataId, out var pendingPayouts) &&
                            pendingPayouts.TryGetValue(payout.Id, out var pendingPayout))
                        {
                            long toCredit = 0;
                            switch (payout.State)
                            {
                                case PayoutState.Completed:

                                    var proof = _lightningLikePayoutHandler.ParseProof(payout) as PayoutLightningBlob;

                                    long? feePaid = null;
                                    if (!string.IsNullOrEmpty(proof?.PaymentHash))
                                    {
                                        if (!lnClients.TryGetValue(payout.StoreDataId, out var lnClient))
                                        {
                                            var store = await _storeRepository.FindStore(payout.StoreDataId);

                                            var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
                                            var id = new PaymentMethodId("BTC", PaymentTypes.LightningLike);
                                            var existing = store.GetSupportedPaymentMethods(_btcPayNetworkProvider)
                                                .OfType<LightningSupportedPaymentMethod>()
                                                .FirstOrDefault(d => d.PaymentId == id);
                                            if (existing?.GetExternalLightningUrl() is { } connectionString)
                                            {
                                                lnClient = _lightningClientFactoryService.Create(connectionString,
                                                    network);
                                            }
                                            else if (existing?.IsInternalNode is true &&
                                                     _lightningNetworkOptions.Value.InternalLightningByCryptoCode
                                                         .TryGetValue(network.CryptoCode,
                                                             out var internalLightningNode))
                                            {
                                                lnClient = _lightningClientFactoryService.Create(internalLightningNode,
                                                    network);
                                            }


                                            lnClients.Add(payout.StoreDataId, lnClient);
                                        }

                                        if (lnClient is not null && proof?.PaymentHash is not null)
                                        {
                                            var p = await lnClient.GetPayment(proof.PaymentHash, CancellationToken);
                                            feePaid = (long) p.Fee.ToUnit(LightMoneyUnit.Satoshi);
                                        }
                                    }

                                    if (feePaid is not null)
                                    {
                                        toCredit = pendingPayout.FeeCharged - feePaid.Value;
                                    }

                                    break;
                                case PayoutState.Cancelled:
                                    toCredit = pendingPayout.PayoutAmount + pendingPayout.FeeCharged;
                                    break;
                            }

                            res.TryAdd(payout.StoreDataId,
                                new CreditDestination(payout.StoreDataId, new Dictionary<string, long>(),
                                    new List<string>()));
                            var credDest = res[payout.StoreDataId];
                            credDest.PayoutsToRemove.Add(payout.Id);

                            credDest.Destcredits.Add(payout.GetBlob(_btcPayNetworkJsonSerializerSettings).Destination,
                                toCredit);
                        }
                    }

                    var tcs = new TaskCompletionSource(cancellationToken);
                    PushEvent(new PayoutCheckResult(res.Values.ToArray(), tcs));
                    //we wait for ProcessEvent to handle this result so that we avoid race conditions.
                    await tcs.Task;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error while checking payouts");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }

        record PayoutCheckResult(CreditDestination[] CreditDestinations, TaskCompletionSource Tcs);

        record CreditDestination(string StoreId, Dictionary<string, long> Destcredits, List<string> PayoutsToRemove);

        private readonly SemaphoreSlim _updateLock = new(1, 1);

        public async Task<PrismSettings> Get(string storeId)
        {
            return _prismSettings.TryGetValue(storeId, out var settings) ? settings : new PrismSettings();
        }

        public async Task<bool> UpdatePrismSettingsForStore(string storeId, PrismSettings updatedSettings,
            bool skipLock = false)
        {
            try
            {
                if (!skipLock)
                    await _updateLock.WaitAsync();
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
                    _updateLock.Release();
            }

            return true; // Indicate that the update succeeded
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
                await _updateLock.WaitAsync(cancellationToken);

                if (evt is PayoutCheckResult payoutCheckResult)
                {
                    foreach (var creditDestination in payoutCheckResult.CreditDestinations)
                    {
                        if (_prismSettings.TryGetValue(creditDestination.StoreId, out var prismSettings))
                        {
                            foreach (var creditDestinationDestcredit in creditDestination.Destcredits)
                            {
                                if (prismSettings.DestinationBalance.TryGetValue(creditDestinationDestcredit.Key,
                                        out var currentBalance))
                                {
                                    prismSettings.DestinationBalance[creditDestinationDestcredit.Key] =
                                        currentBalance + (creditDestinationDestcredit.Value * 1000);
                                }
                                else
                                {
                                    prismSettings.DestinationBalance.Add(creditDestinationDestcredit.Key,
                                        (creditDestinationDestcredit.Value * 1000));
                                }
                            }

                            foreach (var payout in creditDestination.PayoutsToRemove)
                            {
                                prismSettings.PendingPayouts.Remove(payout);
                            }

                            await UpdatePrismSettingsForStore(creditDestination.StoreId, prismSettings, true);
                            if (await CreatePayouts(creditDestination.StoreId, prismSettings))
                            {
                                await UpdatePrismSettingsForStore(creditDestination.StoreId, prismSettings, true);
                            }
                        }
                    }

                    payoutCheckResult.Tcs.SetResult();
                    return;
                }

                if (evt is InvoiceEvent invoiceEvent &&
                    new[] {InvoiceEventCode.Completed, InvoiceEventCode.MarkedCompleted}.Contains(
                        invoiceEvent.EventCode))
                {
                    var pm = invoiceEvent.Invoice.GetPaymentMethod(new PaymentMethodId("BTC", PaymentTypes.LNURLPay));
                    var pmd = pm?.GetPaymentMethodDetails() as LNURLPayPaymentMethodDetails;
                    if (string.IsNullOrEmpty(pmd?.ConsumedLightningAddress))
                    {
                        return;
                    }

                    var address =
                        await _lightningAddressService.ResolveByAddress(pmd.ConsumedLightningAddress.Split("@")[0]);
                    if (address is null || !_prismSettings.TryGetValue(address.StoreDataId, out var prismSettings) ||
                        !prismSettings.Enabled)
                    {
                        return;
                    }

                    var splits = prismSettings.Splits.FirstOrDefault(s => s.Source == address.Username)?.Destinations;
                    if (splits?.Any() is not true)
                    {
                        return;
                    }


                    var msats = pm.Calculate().CryptoPaid.Satoshi * 1000;
                    //compute the sats for each destination  based on splits percentage
                    var msatsPerDestination =
                        splits.ToDictionary(s => s.Destination, s => (long) (msats * (s.Percentage / 100)));

                    prismSettings.DestinationBalance ??= new Dictionary<string, long>();
                    foreach (var (destination, splitMSats) in msatsPerDestination)
                    {
                        if (prismSettings.DestinationBalance.TryGetValue(destination, out var currentBalance))
                        {
                            prismSettings.DestinationBalance[destination] = currentBalance + splitMSats;
                        }
                        else
                        {
                            prismSettings.DestinationBalance.Add(destination, splitMSats);
                        }
                    }

                    await UpdatePrismSettingsForStore(address.StoreDataId, prismSettings, true);
                    if (await CreatePayouts(address.StoreDataId, prismSettings))
                    {
                        await UpdatePrismSettingsForStore(address.StoreDataId, prismSettings, true);
                    }
                }
            }
            catch (Exception e)
            {
                Logs.PayServer.LogWarning(e, "Error while processing prism event");
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private async Task<bool> CreatePayouts(string storeId, PrismSettings prismSettings)
        {
            var result = false;
            foreach (var (destination, amtMsats) in prismSettings.DestinationBalance)
            {
                var amt = amtMsats / 1000;
                if (amt >= prismSettings.SatThreshold)
                {
                    var reserveFee = (long) Math.Max(1, Math.Round(amt * 0.02, 0, MidpointRounding.AwayFromZero));
                    var payoutAmount = amt - reserveFee;
                    if (payoutAmount <= 0)
                    {
                        continue;
                    }

                    var payout = await _pullPaymentHostedService.Claim(new ClaimRequest()
                    {
                        Destination = new LNURLPayClaimDestinaton(destination),
                        PreApprove = true,
                        StoreId = storeId,
                        PaymentMethodId = new PaymentMethodId("BTC", PaymentTypes.LightningLike),
                        Value = Money.Satoshis(payoutAmount).ToDecimal(MoneyUnit.BTC),
                    });
                    if (payout.Result == ClaimRequest.ClaimResult.Ok)
                    {
                        prismSettings.PendingPayouts ??= new();
                        prismSettings.PendingPayouts.Add(payout.PayoutData.Id,
                            new PendingPayout(payoutAmount, reserveFee));
                        prismSettings.DestinationBalance.AddOrReplace(destination,
                            amtMsats - (payoutAmount + reserveFee) * 1000);
                        result = true;
                    }
                }
            }

            return result;
        }
    }
}