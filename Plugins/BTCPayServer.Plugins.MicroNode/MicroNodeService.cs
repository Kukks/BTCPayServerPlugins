#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MicroNode;

public class MicroNodeService : EventHostedServiceBase
{
    private readonly MicroNodeContextFactory _microNodeContextFactory;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
    private readonly StoreRepository _storeRepository;
    private readonly ILogger<MicroNodeService> _logger;
    private readonly PullPaymentHostedService _pullPaymentHostedService;
    private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
    private readonly PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary;
    private static readonly ConcurrentDictionary<string, long> ExpectedCounter = new();
    private readonly TaskCompletionSource _init = new();
    private Dictionary<string, MicroNodeSettings> _ownerSettings;
    private Dictionary<string, MicroNodeStoreSettings?> _storeSettings;
    private readonly BTCPayNetwork _network;
    private readonly IServiceProvider _serviceProvider;
    private static readonly AsyncKeyedLock.AsyncKeyedLocker<string> KeyedLocker = new();

    public const string MasterSettingsKey = "MicroNodeMasterSettings";
    public const string StoreSettingsKey = "MicroNodeStoreSettings";

    public MicroNodeService(MicroNodeContextFactory microNodeContextFactory,
        BTCPayNetworkProvider btcPayNetworkProvider,
        BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
        StoreRepository storeRepository,
        ILogger<MicroNodeService> logger,
        EventAggregator eventAggregator,
        PullPaymentHostedService pullPaymentHostedService,
        IOptions<LightningNetworkOptions> lightningNetworkOptions,
        PaymentMethodHandlerDictionary paymentMethodHandlerDictionary,
        IServiceProvider serviceProvider) : base(eventAggregator, logger)
    {
        _network = btcPayNetworkProvider.BTC;
        _microNodeContextFactory = microNodeContextFactory;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
        _storeRepository = storeRepository;
        _logger = logger;
        _pullPaymentHostedService = pullPaymentHostedService;
        _lightningNetworkOptions = lightningNetworkOptions;
        _paymentMethodHandlerDictionary = paymentMethodHandlerDictionary;
        _serviceProvider = serviceProvider;
    }

    public async Task<MicroTransaction?> MatchRecord(string key, string id)
    {
        await using var ctx = _microNodeContextFactory.CreateContext();
        var transaction = await ctx.MicroTransactions.FirstOrDefaultAsync(t => t.Id == id && t.AccountId == key);
        return transaction ?? null;
    }

    public async Task<MicroTransaction[]> MatchRecords(string key, string[]? toArray)
    {
        if (toArray is null)
        {
            return Array.Empty<MicroTransaction>();
        }

        await using var ctx = _microNodeContextFactory.CreateContext();
        var transactions = await ctx.MicroTransactions.Where(t => toArray.Contains(t.Id) && t.AccountId == key)
            .ToArrayAsync();
        return transactions;
    }

    public async Task<MicroTransaction> UpsertRecord(string key, LightningInvoice invoice)
    {
        return (await UpsertRecords(key, new[] {invoice})).First();
    }
    


    public Task<MicroTransaction[]> UpsertRecords(string key, LightningInvoice[] invoices)
    {
        return UpsertRecords(ConvertToRecords(key, invoices).ToArray());
    }

    public async Task<MicroTransaction> UpsertRecord(string key, Data.PayoutData payout)
    {
        return (await UpsertRecords(key, new[] {payout})).First();
    }

    
    
    private MicroTransaction[] ConvertToRecords(string key, LightningInvoice[] invoices)
    {
        return invoices.Select(invoice => new MicroTransaction()
        {
            Id = invoice.Id,
            AccountId = key,
            Amount = invoice.AmountReceived?.MilliSatoshi ?? invoice.Amount.MilliSatoshi,
            Accounted = invoice.Status == LightningInvoiceStatus.Paid,
            Type = "LightningInvoice",
            Active = invoice.Status == LightningInvoiceStatus.Unpaid
        }).ToArray();
    }
    
    private MicroTransaction[] ConvertToRecords(string key, Data.PayoutData[] payouts)
    {
        return payouts.SelectMany(payout =>
        {
            var b = payout.GetBlob(_btcPayNetworkJsonSerializerSettings);

            List<MicroTransaction> res =
            [
                new()
                {
                    Id = payout.Id,
                    AccountId = key,
                    Amount = -LightMoney.Coins(payout.Amount!.Value).MilliSatoshi,
                    Accounted = payout.State != PayoutState.Cancelled,
                    Active = payout.State is PayoutState.AwaitingApproval or PayoutState.AwaitingPayment
                        or PayoutState.InProgress,
                    Type = "Payout"
                }

            ];

            if (b.Metadata?.TryGetValue("Fee", out var microNode) is true && microNode.Value<decimal>() is { } payoutFee) 
            {
                var fee = LightMoney.Coins(payoutFee);
                res.Add(new MicroTransaction()
                {
                    Id = "FeeOf" + payout.Id,
                    AccountId = key,
                    Amount = -fee.MilliSatoshi,
                    Accounted = payout.State != PayoutState.Cancelled,
                    Active = payout.State is PayoutState.AwaitingApproval or PayoutState.AwaitingPayment
                        or PayoutState.InProgress,
                    Type = "PayoutFee"
                });
            }
            return res.ToArray();
        }).ToArray();
    }
    
    private MicroTransaction[] ConvertToRecords(string key, LightningPayment[] payments)
    {
        return payments.SelectMany(payment =>
        {
            var res = new List<MicroTransaction>
            {
                new()
                {
                    Id = payment.Id,
                    AccountId = key,
                    Amount = -(payment.AmountSent?.MilliSatoshi ?? payment.Amount.MilliSatoshi),
                    Accounted = payment.Status != LightningPaymentStatus.Failed,
                    Type = "LightningPayment",
                    Active = payment.Status is LightningPaymentStatus.Pending or LightningPaymentStatus.Unknown
                }
            };

            if (payment.Fee is { } fee)
            {
                res.Add(new MicroTransaction()
                {
                    Id = "FeeOf" + payment.Id,
                    DependentId = payment.Id,
                    AccountId = key,
                    Amount = -fee.MilliSatoshi,
                    Accounted = payment.Status != LightningPaymentStatus.Failed,
                    Type = "LightningPaymentFee",
                    Active = payment.Status is LightningPaymentStatus.Pending or LightningPaymentStatus.Unknown
                });
            }

            return res.ToArray();
        }).ToArray();
    }
    public Task<MicroTransaction[]> UpsertRecords(string key, Data.PayoutData[] payouts)
    {
        return UpsertRecords(ConvertToRecords(key, payouts));
    }

    public async Task<MicroTransaction> UpsertRecord(string key, LightningPayment payment)
    {
        return (await UpsertRecords(key, new[] {payment})).First();
    }

    public Task<MicroTransaction[]> UpsertRecords(string key, LightningPayment[] payments)
    {
        
        
        return UpsertRecords(ConvertToRecords(key, payments));
    }

    private async Task<MicroTransaction[]> UpsertRecords(MicroTransaction[] transactions)
    {
        ExpectedCounter.TryGetValue(transactions.First().AccountId, out var expected);
        // expected += transactions.Length;
       
        await using var ctx = _microNodeContextFactory.CreateContext();
       var cnt =  await ctx.MicroTransactions.UpsertRange(transactions).RunAsync();
        ExpectedCounter[transactions.First().AccountId] = expected + cnt ;
        await ctx.SaveChangesAsync();
        return transactions;
    }



    public async Task<LightMoney?> GetBalance(string key, CancellationToken cancellation)
    {
        using var keylock = await KeyedLocker.LockAsync(key, cancellation);
        return await GetBalanceCore(key, cancellation);
    }

    private async Task<LightMoney?> GetBalanceCore(string key, CancellationToken cancellation)
    {
        await using var ctx = _microNodeContextFactory.CreateContext();
        for (int i = 0; i < 5; i++)
        {
            var account = await ctx.MicroAccounts.FindAsync(key, cancellation);
            if (account is null)
            {
                return null;
            }

            if (ExpectedCounter.TryGetValue(key, out var expected))
            {
                if (account.BalanceCheckpoint < expected)
                {
                    await Task.Delay(1000, cancellation);
                    continue;
                }
                else
                {
                    ExpectedCounter[key] = account.BalanceCheckpoint;
                }
            }
            else
            {
                ExpectedCounter.TryAdd(key, account.BalanceCheckpoint);
            }

            return new LightMoney(account.Balance, LightMoneyUnit.MilliSatoshi);
        }

        return null;
    }

    public async Task<(MicroNodeSettings, string)?> GetMasterSettingsFromKey(string key,
        CancellationToken cancellation = default)
    {
        await _init.Task.WaitAsync(cancellation);

        if (_keyToMasterStoreId.TryGetValue(key, out var storeId))
        {
            return (_ownerSettings[storeId], storeId);
        }

        await using var ctx = _microNodeContextFactory.CreateContext();
        var acct = await ctx.MicroAccounts.FindAsync(key, cancellation);
        if (acct is null)
        {
            return null;
        }

        var res = _ownerSettings.TryGetValue(acct.MasterStoreId, out var settings)
            ? (settings, acct.MasterStoreId)
            : ((MicroNodeSettings settings, string MasterStoreId)?) null;

        if (res is not null)
        {
            _keyToMasterStoreId.TryAdd(key, acct.MasterStoreId);
        }

        return res;
    }

    public async Task<MicroNodeSettings?> GetMasterSettings(string storeId, CancellationToken cancellation = default)
    {
        await _init.Task.WaitAsync(cancellation);
        return _ownerSettings.TryGetValue(storeId, out var settings) ? settings : null;
    }

    public async Task<ImmutableDictionary<string, MicroNodeSettings>> GetMasterSettings(
        CancellationToken cancellation = default)
    {
        await _init.Task.WaitAsync(cancellation);
        return _ownerSettings.Where(pair => pair.Value.Enabled).ToImmutableDictionary();
    }

    public async Task<MicroNodeStoreSettings?> GetStoreSettings(string storeId,
        CancellationToken cancellation = default)
    {
        await _init.Task.WaitAsync(cancellation);
        return _storeSettings.TryGetValue(storeId, out var settings) ? settings : null;
    }

    public async Task<MicroNodeStoreSettings?> GetStoreSettingsFromKey(string key,
        CancellationToken cancellation = default)
    {
        await _init.Task.WaitAsync(cancellation);
        var res = _storeSettings.FirstOrDefault(pair => pair.Value?.Key == key);
        return res.Value;
    }

    public async Task<ILightningClient?> GetMasterLightningClientFromKey(string key)
    {
        var settings = await GetMasterSettingsFromKey(key);
        if (settings is null)
        {
            return null;
        }

        return await GetMasterLightningClient(settings.Value.Item2);
    }


    public async Task<ILightningClient?> GetMasterLightningClient(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId);
        if (store is null)
        {
            return null;
        }

        var pmi = PaymentTypes.LN.GetPaymentMethodId(_network.CryptoCode);
        var lightningConnectionString = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmi, _paymentMethodHandlerDictionary)
            ?.CreateLightningClient(_network,
                _lightningNetworkOptions.Value, _serviceProvider.GetService<LightningClientFactoryService>());
        return lightningConnectionString;
    }

    public async Task<MicroTransaction> InitiatePayment(string key, string paymentId, LightMoney amount, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            throw new InvalidOperationException("Cannot pay a negative amount");
        }
        using var locker = await KeyedLocker.LockAsync(key, cancellationToken);
        
        await using var ctx = _microNodeContextFactory.CreateContext();
        var matched = await ctx.MicroTransactions.Include(transaction => transaction.Account).Where(transaction => transaction.Id == paymentId)
            .ToArrayAsync(cancellationToken: cancellationToken);
        
        var ourRecord = matched.FirstOrDefault(transaction => transaction.AccountId == key);

        // already successfully paid
        if(ourRecord is {Active: false, Accounted: true} && ourRecord.Amount == -amount.MilliSatoshi)
        {
            return ourRecord;
            //previous payment failure
        }else if(ourRecord is {Active: false, Accounted: false} && ourRecord.Amount == -amount.MilliSatoshi)
        {
            return ourRecord;
        }else if (ourRecord is not null)
        {
            throw new InvalidOperationException($"A record with this payment hash was already present- Accounted:{ourRecord.Accounted} Active:{ourRecord.Active} Amount:{ourRecord.Amount}");
        }

        var masterStoreId = await GetMasterSettingsFromKey(key, cancellationToken);

        var isInternal = matched.Any(transaction =>
            transaction.AccountId != key && transaction.Amount >= 0 &&
            transaction.Account.MasterStoreId == masterStoreId.Value.Item2);
        
        var balance = SpendableExternalBalance(await GetBalanceCore(key, cancellationToken), isInternal, out var fee);
        
        if(balance < amount)
        {
            throw new InvalidOperationException("Insufficient balance");
        }

        var r = await UpsertRecords(new MicroTransaction[]
        {
            new MicroTransaction()
            {
                Id = paymentId,
                AccountId = key,
                Amount = -amount.MilliSatoshi,
                Accounted = true,
                Type = "LightningPayment",
                Active = true
            },
            new MicroTransaction()
            {
                Id = "FeeOf" + paymentId,
                AccountId = key,
                Amount = -fee.MilliSatoshi,
                Accounted = true,
                Type = "LightningPaymentFee",
                Active = true
            }
        });
        return r.First(transaction => transaction.Type == "LightningPayment");
    }

    private LightMoney? SpendableExternalBalance(LightMoney? balance,bool  isInternal, out LightMoney fee)
    {
        if (isInternal || balance < LightMoney.Satoshis(10000))
        {
            fee = LightMoney.Zero;
            return balance;
        }

        var maxFeeAmount = (long)Math.Round(balance.MilliSatoshi * 0.03m);
        fee = LightMoney.MilliSatoshis(maxFeeAmount);
        return balance - fee;
    }


    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Migrating MicroNode database");
        await using var ctx = _microNodeContextFactory.CreateContext();
        var pendingMigrations = await ctx.Database.GetPendingMigrationsAsync(cancellationToken: cancellationToken);
        if (pendingMigrations.Any())
        {
            _logger.LogInformation("Applying {Count} migrations", pendingMigrations.Count());
            await ctx.Database.MigrateAsync(cancellationToken: cancellationToken);
        }
        else
        {
            _logger.LogInformation("No migrations to apply");
        }

        _ownerSettings = await _storeRepository.GetSettingsAsync<MicroNodeSettings>(MasterSettingsKey);
        _storeSettings = await _storeRepository.GetSettingsAsync<MicroNodeStoreSettings>(StoreSettingsKey);
        _init.TrySetResult();
        PushEvent(new CheckActiveTransactions());
        PushEvent(new CreatePayoutEvt());
        await base.StartAsync(cancellationToken);
    }

    class CheckActiveTransactions;

    class CreatePayoutEvt;

    
    
    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is PayoutEvent payoutEvent)
        {
            await using var ctx = _microNodeContextFactory.CreateContext();
            var matchedTx = await ctx.MicroTransactions.SingleOrDefaultAsync(
                transaction => transaction.Amount < 0 && transaction.Id == payoutEvent.Payout.Id,
                cancellationToken: cancellationToken);

            if (matchedTx is null)
            {
                return;
            }

            await UpsertRecord(matchedTx.AccountId, payoutEvent.Payout);
        }
        else if (evt is CheckActiveTransactions)
        {
            
            Dictionary<string, ILightningClient> masterToLightningClients = new();
            foreach (var (key, value) in _ownerSettings)
            {
                //this simplistic approach should catch most cases
                if(!masterToLightningClients.TryGetValue(key, out var lnClient))
                {
                    lnClient = await GetMasterLightningClient(key);
                    if (lnClient is null)
                    {
                        continue;
                    }
                    masterToLightningClients.Add(key, lnClient);
                }
                await lnClient.ListInvoices(cancellationToken);
                await lnClient.ListPayments(cancellationToken);
            }
            
            await using var ctx = _microNodeContextFactory.CreateContext();
            var activeTransactions = await ctx.MicroTransactions.Where(transaction => transaction.Active)
                .ToArrayAsync(cancellationToken);

            var transactionsPayouts = activeTransactions.Where(transaction => transaction.Type == "Payout").ToArray();

            var payouts = await _pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
            {
                PayoutIds = transactionsPayouts.Select(p => p.Id).ToArray()
            });

            var upsertRecords = new List<MicroTransaction>();
            foreach (var keyGroup in transactionsPayouts.GroupBy(transaction => transaction.AccountId))
            {
                var keyPayouts = payouts.Where(p => keyGroup.Any(transaction => p.Id == transaction.Id)).ToArray();
                upsertRecords.AddRange(ConvertToRecords(keyGroup.Key, keyPayouts));
            }
            

            var transactionsInvoices = activeTransactions.Where(transaction => transaction.Type == "LightningInvoice")
                .ToArray();

            foreach (var keyGroup in transactionsInvoices.GroupBy(transaction => transaction.AccountId))
            {
                if (!masterToLightningClients.TryGetValue(keyGroup.Key, out var lightningClient))
                {
                    continue;
                }
                await Task.WhenAll(keyGroup.Select(async transaction =>
                {
                    try
                    {
                        var invoice = await lightningClient.GetInvoice(transaction.Id, cancellationToken);
                        if (invoice is null && InvalidateAfterRetries(transaction.Id, 12))
                        {
                            transaction.Active = false;
                        }else if(invoice is not null)
                        {
                            _retries.TryRemove(transaction.Id, out _);
                            upsertRecords.AddRange(ConvertToRecords(keyGroup.Key, new []{invoice}));
                        }
                    }
                    catch (Exception e)
                    {
                        if (InvalidateAfterRetries(transaction.Id, 12))
                        {
                            transaction.Active = false;
                        }
                    }
                }));
            }
            
            var transactionPayments = activeTransactions.Where(transaction => transaction.Type == "LightningPayment")
                .ToArray();

            foreach (var keyGroup in transactionPayments.GroupBy(transaction => transaction.AccountId))
            {
                if (!masterToLightningClients.TryGetValue(keyGroup.Key, out var lightningClient))
                {
                    continue;
                }
                await Task.WhenAll(keyGroup.Select(async transaction =>
                {
                    try
                    {
                        var invoice = await lightningClient.GetPayment(transaction.Id, cancellationToken);
                        if (invoice is null && InvalidateAfterRetries(transaction.Id, 60))
                        {
                            transaction.Active = false;
                        }else if(invoice is not null)
                        {
                            _retries.TryRemove(transaction.Id, out _);
                            upsertRecords.AddRange(ConvertToRecords(keyGroup.Key, new []{invoice}));
                        }
                    }
                    catch (Exception e)
                    {
                        if (InvalidateAfterRetries(transaction.Id, 60))
                        {
                            transaction.Active = false;
                        }
                    }
                }));
            }

            _ = Task.WhenAny(Task.Delay(TimeSpan.FromMinutes(1), cancellationToken)).ContinueWith(
                task =>
                {
                    if (!task.IsCanceled)
                        PushEvent(new CheckActiveTransactions());
                }, cancellationToken);

            return;
        }
        else if (evt is CreatePayoutEvt)
        {
            await using var ctx = _microNodeContextFactory.CreateContext();
            var autoForwardThreshold = LightMoney.Satoshis(10000).MilliSatoshi;
            var balances = await ctx.MicroAccounts.Where(account => account.Balance > autoForwardThreshold)
                .ToArrayAsync(cancellationToken: cancellationToken);
            foreach (var masterClients in balances.GroupBy(account => account.MasterStoreId))
            {
                var lnCLient = await GetMasterLightningClient(masterClients.Key);
                if (lnCLient is null)
                {
                    continue;
                }

                foreach (var client in masterClients)
                {
                    await KeyedLocker.TryLockAsync(client.Key, async () =>
                    {
                        var storeId = _storeSettings.FirstOrDefault(pair => pair.Value.Key == client.Key);
                        var destination = storeId.Value?.ForwardDestination;
                        if (destination is null)
                        {
                            return;
                        }

                        var balance = await GetBalanceCore(client.Key, CancellationToken.None);
                        if (balance is null)
                        {
                            return;
                        }
                        var payout = await _pullPaymentHostedService.Claim(new ClaimRequest()
                        {
                            ClaimedAmount = LightMoney.MilliSatoshis(balance.MilliSatoshi).ToDecimal(LightMoneyUnit.BTC),
                            StoreId = masterClients.Key,
                            Destination = new LNURLPayClaimDestinaton(destination),
                            PreApprove = true,
                            PayoutMethodId = PayoutTypes.LN.GetPayoutMethodId("BTC"),
                            Metadata = JObject.FromObject(new
                            {
                                Source = $"MicroNode on store {storeId.Key}"
                            })
                        });
                        if (payout.PayoutData is not null)
                        {
                            await UpsertRecord(client.Key, payout.PayoutData);
                        }
                    }, -1, cancellationToken);
                }
            }
            
            _ = Task.WhenAny(Task.Delay(TimeSpan.FromMinutes(1), cancellationToken)).ContinueWith(
                task =>
                {
                    if (!task.IsCanceled)
                        PushEvent(new CreatePayoutEvt());
                }, cancellationToken);

        }else if (evt is StoreRemovedEvent storeRemovedEvent)
        {
           _storeSettings.Remove(storeRemovedEvent.StoreId);
           _ownerSettings.Remove(storeRemovedEvent.StoreId);
        }


        await base.ProcessEvent(evt, cancellationToken);
    }
    
    private readonly ConcurrentDictionary<string, int> _retries = new ();
    private bool InvalidateAfterRetries(string id, int max)
    {
       return  _retries.AddOrUpdate(id, 1, (_, i) => i + 1) >= max;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<PayoutEvent>();
        Subscribe<CreatePayoutEvt>();
        Subscribe<StoreRemovedEvent>();
        Subscribe<CheckActiveTransactions>();
        base.SubscribeToEvents();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
    }

    private readonly ConcurrentDictionary<string, string> _keyToMasterStoreId = new();

    public async Task Set(string storeId, MicroNodeStoreSettings? settings, string? masterStoreId = null)
    {
        await _init.Task;
        if (settings is null)
        {
            _storeSettings.Remove(storeId);
            _keyToMasterStoreId.TryRemove(settings!.Key, out _);
            await _storeRepository.UpdateSetting(storeId, StoreSettingsKey, (MicroNodeStoreSettings?) null);
        }
        else
        {
            _storeSettings[storeId] = settings;
            if (masterStoreId is not null)
            {
                _keyToMasterStoreId[settings.Key] = masterStoreId;
            }

            await _storeRepository.UpdateSetting(storeId, StoreSettingsKey, settings);
            await using var ctx = _microNodeContextFactory.CreateContext();
            await ctx.MicroAccounts.Upsert(new MicroAccount()
            {
                Balance = 0,
                BalanceCheckpoint = 0,
                MasterStoreId = masterStoreId,
                Key = settings.Key,
            }).NoUpdate().RunAsync();
        }
    }

    public async Task SetMaster(string storeId, MicroNodeSettings? settings)
    {
        await _init.Task;
        if (settings is null)
        {
            _ownerSettings.Remove(storeId);
            await _storeRepository.UpdateSetting(storeId, MasterSettingsKey, (MicroNodeSettings?) null);
        }
        else
        {
            _ownerSettings[storeId] = settings;
            await _storeRepository.UpdateSetting(storeId, MasterSettingsKey, settings);
            await using var ctx = _microNodeContextFactory.CreateContext();
        }
    }

    public async Task<MicroAccount[]> GetMasterLiabilities(string storeId, bool includeTxs)
    {
        await using var ctx = _microNodeContextFactory.CreateContext();
        var query = ctx.MicroAccounts.AsQueryable();
        if (includeTxs)
        {
            query = query.Include(account => account.Transactions);
        }

        return await query.Where(account => account.MasterStoreId == storeId).ToArrayAsync();
    }
    public async Task<MicroTransaction[]> GetTransactions(string storeId)
    {
        if(!_storeSettings.TryGetValue(storeId, out var settings))
        {
            return null;
        }
        await using var ctx = _microNodeContextFactory.CreateContext();
        return await ctx.MicroTransactions.Where(t => t.AccountId == settings!.Key).ToArrayAsync();
    }
}