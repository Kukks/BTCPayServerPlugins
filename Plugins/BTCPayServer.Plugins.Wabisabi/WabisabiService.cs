using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Services;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Wabisabi
{
    public class WabisabiService
    {
        private readonly IStoreRepository _storeRepository;
        private readonly WabisabiCoordinatorClientInstanceManager _coordinatorClientInstanceManager;
        private readonly WalletProvider _walletProvider;
        private readonly WalletRepository _walletRepository;
        private readonly PayoutProcessorService _payoutProcessorService;
        private readonly EventAggregator _eventAggregator;
        private string[] _ids => _coordinatorClientInstanceManager.HostedServices.Keys.ToArray();

        public WabisabiService( IStoreRepository storeRepository, 
            WabisabiCoordinatorClientInstanceManager coordinatorClientInstanceManager,
            WalletProvider walletProvider,
            WalletRepository walletRepository,PayoutProcessorService payoutProcessorService, EventAggregator eventAggregator)
        {
            _storeRepository = storeRepository;
            _coordinatorClientInstanceManager = coordinatorClientInstanceManager;
            _walletProvider = walletProvider;
            _walletRepository = walletRepository;
            _payoutProcessorService = payoutProcessorService;
            _eventAggregator = eventAggregator;
        }
        
        public async Task<WabisabiStoreSettings> GetWabisabiForStore(string storeId)
        {
            
            var res = await  _storeRepository.GetSettingAsync<WabisabiStoreSettings>(storeId, nameof(WabisabiStoreSettings));
            res ??= new WabisabiStoreSettings();
            res.Settings = res.Settings.Where(settings => _ids.Contains(settings.Coordinator)).ToList();
            foreach (var wabisabiCoordinatorManager in _coordinatorClientInstanceManager.HostedServices)
            {
                if (res.Settings.All(settings => settings.Coordinator != wabisabiCoordinatorManager.Key))
                {
                    res.Settings.Add(new WabisabiStoreCoordinatorSettings()
                    {
                        Coordinator = wabisabiCoordinatorManager.Key,
                    });
                }
            }

            return res;
        }

        public async Task SetWabisabiForStore(string storeId, WabisabiStoreSettings wabisabiSettings)
        {
            var paybatching = wabisabiSettings.PlebMode || wabisabiSettings.BatchPayments &&
                wabisabiSettings.Settings.Any(settings => settings.Enabled);
            foreach (var setting in wabisabiSettings.Settings)
            {
                if (setting.Enabled) continue;
                if(_coordinatorClientInstanceManager.HostedServices.TryGetValue(setting.Coordinator, out var coordinator))
                    _ = coordinator.StopWallet(storeId);
            }
   
            if (wabisabiSettings.Settings.All(settings => !settings.Enabled))
            {
                
                await _storeRepository.UpdateSetting<WabisabiStoreSettings>(storeId, nameof(WabisabiStoreSettings), null!);
            }
            else
            {
                await _storeRepository.UpdateSetting<WabisabiStoreSettings>(storeId, nameof(WabisabiStoreSettings), wabisabiSettings!);
            }
            
            await _walletProvider.SettingsUpdated(storeId, wabisabiSettings);
           // var existingProcessor = (await  _payoutProcessorService.GetProcessors(new PayoutProcessorService.PayoutProcessorQuery()
           // {
           //     Stores = new[] {storeId},
           //     Processors = new[] {"Wabisabi"},
           //
           // })).FirstOrDefault();
           //  _eventAggregator.Publish(new PayoutProcessorUpdated()
           //  {
           //      Id = existingProcessor?.Id,
           //      Data = paybatching? new PayoutProcessorData()
           //      {
           //          Id = existingProcessor?.Id,
           //          Processor = "Wabisabi",
           //          StoreId = storeId,
           //          PaymentMethod = "BTC",
           //      }: null
           //  });
        }
        

        public async Task<List<BTCPayWallet.CoinjoinData>> GetCoinjoinHistory(string storeId)
        {
            return (await _walletRepository.GetWalletObjects(
                    new GetWalletObjectsQuery(new WalletId(storeId, "BTC"))
                    {
                        Type = "coinjoin"
                    })).Values.Where(data => !string.IsNullOrEmpty(data.Data))
                .Select(data => JObject.Parse(data.Data).ToObject<BTCPayWallet.CoinjoinData>())
                .OrderByDescending(tuple => tuple.Timestamp).ToList();
        }
    }
    
}
