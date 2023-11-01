#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.Breez;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.FixedFloat
{
    public class BreezSettings
    {
        public string InviteCode { get; set; }
        public string Mnemonic { get; set; }
        public string ApiKey { get; set; }
    }
    
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
                    var client = new BreezLightningClient(settings.InviteCode, settings.ApiKey, GetWorkDir(),
                        _btcPayNetworkProvider.BTC.NBitcoinNetwork, settings.Mnemonic);
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
                _settings.Remove(storeId);
                
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

        public BreezLightningClient? GetClient(string storeId)
        {
            _clients.TryGetValue(storeId, out var client);
            return client;
        }
    }
    
    public class BreezPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=1.12.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<BreezService>();
            applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BreezService>());
            applicationBuilder.AddSingleton<BreezLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BreezLightningConnectionStringHandler>());
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/BreezNav",
                "store-integrations-nav"));
            base.Execute(applicationBuilder);
        }
    }
}