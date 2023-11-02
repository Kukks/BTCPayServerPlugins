#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.Breez;

public class BreezService:IHostedService
{
    private readonly StoreRepository _storeRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    private readonly ILogger _logger;
    private Dictionary<string, BreezSettings> _settings;
    private Dictionary<string, BreezLightningClient> _clients = new();

    public BreezService(StoreRepository storeRepository,
        IOptions<DataDirectories> dataDirectories, 
        BTCPayNetworkProvider btcPayNetworkProvider, 
        ILogger<BreezService> logger)
    {
        _storeRepository = storeRepository;
        _dataDirectories = dataDirectories;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _logger = logger;
    }

    private string GetWorkDir()
    {
        var dir =  _dataDirectories.Value.DataDir;
        return Path.Combine(dir, "Plugins", "Breez");
    }

    TaskCompletionSource tcs = new();
    public async Task StartAsync(CancellationToken cancellationToken)
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
                Directory.CreateDirectory(GetWorkDir());
                var client = new BreezLightningClient(settings.InviteCode, settings.ApiKey, GetWorkDir(),
                    network, settings.Mnemonic);
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
            var existing = data?.GetSupportedPaymentMethods(_btcPayNetworkProvider)
                .OfType<LightningSupportedPaymentMethod>().FirstOrDefault(method =>
                    method.CryptoCode == "BTC" && method.PaymentId.PaymentType == LightningPaymentType.Instance);
            var isBreez = existing?.GetExternalLightningUrl() == $"type=breez;key={oldSettings.PaymentKey}";
            if (isBreez)
            {
                data.SetSupportedPaymentMethod(new PaymentMethodId("BTC", LightningPaymentType.Instance), null );
                await _storeRepository.UpdateStore(data);
            }

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
        if(storeId is null)
            return null;
        _clients.TryGetValue(storeId, out var client);
        return client;
    }  
    public BreezLightningClient? GetClientByPaymentKey(string? paymentKey)
    {
        if(paymentKey is null)
            return null;
        var match = _settings.FirstOrDefault(pair => pair.Value.PaymentKey == paymentKey).Key;
        return GetClient(match);
    }
}