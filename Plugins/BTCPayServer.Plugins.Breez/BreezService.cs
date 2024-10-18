#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.Breez;

public class BreezService:EventHostedServiceBase
{
    private readonly StoreRepository _storeRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly IServiceProvider _serviceProvider;
    private PaymentMethodHandlerDictionary _paymentMethodHandlerDictionary => _serviceProvider.GetRequiredService<PaymentMethodHandlerDictionary>();
    private readonly ILogger _logger;
    private Dictionary<string, BreezSettings> _settings;
    private Dictionary<string, BreezLightningClient> _clients = new();

    public BreezService(
        EventAggregator eventAggregator,
        StoreRepository storeRepository,
        IOptions<DataDirectories> dataDirectories, 
        IServiceProvider serviceProvider,
        ILogger<BreezService> logger) : base(eventAggregator, logger)
    {
        _storeRepository = storeRepository;
        _dataDirectories = dataDirectories;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<StoreRemovedEvent>();
        base.SubscribeToEvents();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is StoreRemovedEvent storeRemovedEvent)
        {
            await Handle(storeRemovedEvent.StoreId, null);
            _settings.Remove(storeRemovedEvent.StoreId);
        }
        await base.ProcessEvent(evt, cancellationToken);
    }

    public  string GetWorkDir(string storeId)
    {
        var dir =  _dataDirectories.Value.DataDir;
        return Path.Combine(dir, "Plugins", "Breez",storeId);
    }

    TaskCompletionSource tcs = new();
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = (await _storeRepository.GetSettingsAsync<BreezSettings>("Breez")).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);
        foreach (var keyValuePair in _settings)
        {
            try
            {

                await Handle(keyValuePair.Key, keyValuePair.Value);
            }
            catch (Exception e)
            {
            }
        }
        tcs.TrySetResult();
        await base.StartAsync(cancellationToken);
    }

    public async Task<BreezSettings?> Get(string storeId)
    {
        await tcs.Task;
        _settings.TryGetValue(storeId, out var settings);
        
        return settings;
    }

    public  async Task<BreezLightningClient?> Handle(string? storeId, BreezSettings? settings)
    {
        if (settings is null)
        {
            if (storeId is not null && _clients.Remove(storeId, out var client))
            {
                client.Dispose();
            }
        }
        else
        {
            try
            {
                var network = Network.Main; // _btcPayNetworkProvider.BTC.NBitcoinNetwork;
                var dir = GetWorkDir(storeId);
                Directory.CreateDirectory(dir);
                settings.PaymentKey ??= Guid.NewGuid().ToString();
                var client = new BreezLightningClient(settings.InviteCode, settings.ApiKey, dir,
                    network, new Mnemonic(settings.Mnemonic), settings.PaymentKey);
                if (storeId is not null)
                {
                    _clients.AddOrReplace(storeId, client);
                }
                return client;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not create Breez client");
                throw;
            }
        }

        return null;
    }

    public async Task Set(string storeId, BreezSettings? settings)
    {
            
        var result = await Handle(storeId, settings);
        await _storeRepository.UpdateSetting(storeId, "Breez", settings!);
        if (settings is null)
        {
            _settings.Remove(storeId, out var oldSettings );
            var data = await _storeRepository.FindStore(storeId);
            var pmi = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var existing =
                data?.GetPaymentMethodConfig<LightningPaymentMethodConfig>(pmi, _paymentMethodHandlerDictionary);
            var isBreez = existing?.GetExternalLightningUrl() == $"type=breez;key={oldSettings.PaymentKey}";
            if (isBreez)
            {
                data.SetPaymentMethodConfig(_paymentMethodHandlerDictionary[pmi], null );
                await _storeRepository.UpdateStore(data);
            }
            Directory.Delete(GetWorkDir(storeId), true);

        }
        else if(result is not null )
        {
            _settings.AddOrReplace(storeId, settings);
        }
            
            
    }
        
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _clients.Values.ToList().ForEach(c => c.Dispose());
    }

    public BreezLightningClient? GetClient(string? storeId)
    {
        
        tcs.Task.GetAwaiter().GetResult();
        if(storeId is null)
            return null;
        _clients.TryGetValue(storeId, out var client);
        return client;
    }  
    public BreezLightningClient? GetClientByPaymentKey(string? paymentKey)
    {
        tcs.Task.GetAwaiter().GetResult();
        if(paymentKey is null)
            return null;
        var match = _settings.FirstOrDefault(pair => pair.Value.PaymentKey == paymentKey).Key;
        return GetClient(match);
    }
}