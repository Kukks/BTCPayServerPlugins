using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Common;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Wabisabi;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using NBXplorer;
using Newtonsoft.Json.Linq;
using NNostr.Client;
using NNostr.Client.Protocols;
using WalletWasabi.Bases;
using WalletWasabi.BitcoinCore.Rpc;
using WalletWasabi.Cache;
using WalletWasabi.Services;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend;
using WalletWasabi.WabiSabi.Backend.Statistics;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WebClients.Wasabi;

namespace WalletWasabi.Backend.Controllers;

public class WabisabiCoordinatorService : PeriodicRunner
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IFeeProviderFactory _feeProviderFactory;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly IExplorerClientProvider _clientProvider;
    private readonly IMemoryCache _memoryCache;
    private readonly WabisabiCoordinatorClientInstanceManager _instanceManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WabisabiCoordinatorService> _logger;

    public readonly IdempotencyRequestCache IdempotencyRequestCache;

    public bool Started => HostedServices.IsStartAllAsyncStarted;
    private HostedServices HostedServices { get; } = new();
    public WabiSabiCoordinator WabiSabiCoordinator { get; private set; }

    public WabisabiCoordinatorService(
        BTCPayNetworkProvider networkProvider,
        IFeeProviderFactory feeProviderFactory,
        ISettingsRepository settingsRepository,
        IOptions<DataDirectories> dataDirectories,
        IExplorerClientProvider clientProvider, 
        IMemoryCache memoryCache,
        WabisabiCoordinatorClientInstanceManager instanceManager,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        ILogger<WabisabiCoordinatorService> logger,
        WabiSabiConfig.CoordinatorScriptResolver coordinatorScriptResolver) : base(TimeSpan.FromMinutes(15))
    {
        _networkProvider = networkProvider;
        _feeProviderFactory = feeProviderFactory;
        _settingsRepository = settingsRepository;
        _dataDirectories = dataDirectories;
        _clientProvider = clientProvider;
        _memoryCache = memoryCache;
        _instanceManager = instanceManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _coordinatorScriptResolver = coordinatorScriptResolver;
        _socks5HttpClientHandler = serviceProvider.GetRequiredService<Socks5HttpClientHandler>();
        IdempotencyRequestCache = new(memoryCache);
    }
    

    private WabisabiCoordinatorSettings cachedSettings;
    private readonly Socks5HttpClientHandler _socks5HttpClientHandler;
    private readonly WabiSabiConfig.CoordinatorScriptResolver _coordinatorScriptResolver;

    public async Task<WabisabiCoordinatorSettings> GetSettings()
    {
        return cachedSettings ??= (await _settingsRepository.GetSettingAsync<WabisabiCoordinatorSettings>(
            nameof(WabisabiCoordinatorSettings))) ?? new WabisabiCoordinatorSettings();
    }

    public async Task UpdateSettings(WabisabiCoordinatorSettings wabisabiCoordinatorSettings)
    {
        var existing = await GetSettings();
        cachedSettings = wabisabiCoordinatorSettings;
        if (existing.Enabled != wabisabiCoordinatorSettings.Enabled)
        {
            switch (wabisabiCoordinatorSettings.Enabled)
            {
                case true:
                    await StartCoordinator(CancellationToken.None);
                    break;
                case false:

                    await StopAsync(CancellationToken.None);
                    break;
            }
        }
        else if (existing.Enabled)
        {
            if (_instanceManager.HostedServices.TryGetValue("local", out var instance))
            {
                instance.TermsConditions = wabisabiCoordinatorSettings.TermsConditions;
            }
            if(wabisabiCoordinatorSettings.Enabled &&
               (existing.NostrIdentity != wabisabiCoordinatorSettings.NostrIdentity || existing.NostrRelay != wabisabiCoordinatorSettings.NostrRelay))
            {
                var nostr = HostedServices.Get<NostrWabisabiApiServer>();
                nostr.UpdateSettings(wabisabiCoordinatorSettings);
                await nostr.StopAsync(CancellationToken.None);
                await nostr.StartAsync(CancellationToken.None);
            }

        }

        
        await _settingsRepository.UpdateSetting(wabisabiCoordinatorSettings, nameof(WabisabiCoordinatorSettings));
        
        TriggerRound();
    }

    public class BtcPayRpcClient : CachedRpcClient
    {
        private readonly ExplorerClient _explorerClient;
        private readonly Stopwatch _uptime;
        private readonly IFeeProvider _feeProvider;

        public BtcPayRpcClient(IFeeProvider feeProvider, RPCClient rpc, IMemoryCache cache, ExplorerClient explorerClient) : base(rpc, cache)
        {
            _feeProvider = feeProvider;
            _explorerClient = explorerClient;
            _uptime = Stopwatch.StartNew();
        }

        public override async Task<Transaction> GetRawTransactionAsync(uint256 txid, bool throwIfNotFound = true,
            CancellationToken cancellationToken = default)
        {
            var result = (await _explorerClient.GetTransactionAsync(txid, cancellationToken))?.Transaction;
            if (result is null && throwIfNotFound)
            {
                throw new RPCException(RPCErrorCode.RPC_MISC_ERROR, "tx not found", new RPCResponse(new JObject()));
            }

            return result;
        }

        public override async Task<uint256> SendRawTransactionAsync(Transaction transaction,
            CancellationToken cancellationToken = default)
        {
            var result = await _explorerClient.BroadcastAsync(transaction, cancellationToken);
            if (!result.Success)
            {
                throw new RPCException((RPCErrorCode)result.RPCCode, result.RPCMessage, null);
            }

            return transaction.GetHash();
        }

        public override async Task<int> GetBlockCountAsync(CancellationToken cancellationToken = default)
        {   
            var result = await _explorerClient.GetStatusAsync(cancellationToken);
            return result.BitcoinStatus.Blocks;
        }
        public override Task<TimeSpan> UptimeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_uptime.Elapsed);
        }

        public override async Task<EstimateSmartFeeResponse> EstimateSmartFeeAsync(int confirmationTarget,
            EstimateSmartFeeMode estimateMode = EstimateSmartFeeMode.Conservative,
            CancellationToken cancellationToken = default)
        {

            string cacheKey = $"{nameof(EstimateSmartFeeAsync)}:{confirmationTarget}:{estimateMode}";

            return await IdempotencyRequestCache.GetCachedResponseAsync(
                cacheKey,
                action: async (_, cancellationToken) =>
                {
                    
                    var rate = await _feeProvider.GetFeeRateAsync(confirmationTarget);
                    
                    // var result = await _explorerClient.GetFeeRateAsync(confirmationTarget, new FeeRate(100m), cancellationToken);
                    return new EstimateSmartFeeResponse() {FeeRate = rate};
                },
                options: CacheOptionsWithExpirationToken(size: 1, expireInSeconds: 60),
                cancellationToken).ConfigureAwait(false);

        }


        public override async Task<BlockchainInfo> GetBlockchainInfoAsync(CancellationToken cancellationToken = default)
        {
            var status = await _explorerClient.GetStatusAsync(cancellationToken);

            return new BlockchainInfo()
            {
                Chain = _explorerClient.Network.NBitcoinNetwork,
                InitialBlockDownload = status.BitcoinStatus.VerificationProgress > 0.999,
                Blocks = (ulong) status.BitcoinStatus.Blocks,
                Headers = (ulong) status.BitcoinStatus.Headers,
            };
        }

        public override async Task<PeerInfo[]> GetPeersInfoAsync(CancellationToken cancellationToken = default)
        {
            return new[] {new PeerInfo()};
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var explorerClient = _clientProvider.GetExplorerClient("BTC");
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        var coordinatorParameters =
            new CoordinatorParameters(Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "Coinjoin"));
        var coinJoinScriptStore = CoinJoinScriptStore.LoadFromFile(coordinatorParameters.CoinJoinScriptStoreFilePath);
        var rpc = new BtcPayRpcClient(_feeProviderFactory.CreateFeeProvider(network),explorerClient.RPCClient, _memoryCache, explorerClient);

        WabiSabiCoordinator = new WabiSabiCoordinator(coordinatorParameters, rpc, coinJoinScriptStore,
            _httpClientFactory, _coordinatorScriptResolver);
        HostedServices.Register<WabiSabiCoordinator>(() => WabiSabiCoordinator, "WabiSabi Coordinator");
        
        var settings = await GetSettings();
        WabisabiApiServer = new NostrWabisabiApiServer(WabiSabiCoordinator.Arena, settings, _logger);
        HostedServices.Register<NostrWabisabiApiServer>(() => WabisabiApiServer, "WabiSabi Coordinator Nostr");

        if (settings.Enabled)
        {
            _ = StartCoordinator(cancellationToken);
        }
        if (settings.DiscoveredCoordinators?.Any() is true)
        {
            foreach (var discoveredCoordinator in settings.DiscoveredCoordinators)
            {
                _instanceManager.AddCoordinator(discoveredCoordinator.Name, discoveredCoordinator.Name, _ => discoveredCoordinator.Uri, null, discoveredCoordinator.Description );
            }
        }
        await base.StartAsync(cancellationToken);
    }

    public NostrWabisabiApiServer WabisabiApiServer { get; set; }

    public async Task StartCoordinator(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting local coordinator");
        await HostedServices.StartAllAsync(cancellationToken);
        if (_instanceManager.HostedServices.TryGetValue("local", out var instance))
        {
            instance.WasabiCoordinatorStatusFetcher.OverrideConnected = null;
        }
       
        _instanceManager.AddCoordinator("Local Coordinator", 
            "local", _ => null, 
            null,
            cachedSettings.TermsConditions, 
            cachedSettings.CoordinatorDescription
            );
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_instanceManager.HostedServices.TryGetValue("local", out var instance))
        {
            instance.WasabiCoordinatorStatusFetcher.OverrideConnected = false;
        }
        await HostedServices.StopAllAsync(cancellationToken);

    }

    protected override async Task ActionAsync(CancellationToken cancel)
    {
        var network = _clientProvider.GetExplorerClient("BTC").Network.NBitcoinNetwork;
        var s = await GetSettings();
       
        Uri uri = s.UriToAdvertise;
        if (s.Enabled && !string.IsNullOrEmpty(s.NostrIdentity) && s.NostrRelay is not null &&
            s.UriToAdvertise is not null)
        {
            var k = s.GetKey();
            
            if(s.UriToAdvertise.Scheme.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
            {
                //make sure the end is with plugins/wabisabi-coordinator/
                var uriB = new UriBuilder(uri);
                
                uriB.Path = uriB.Path.Replace("plugins/wabisabi-coordinator", "").TrimEnd('/') + "/plugins/wabisabi-coordinator/";
                uri= uriB.Uri;
            }
            
            //verify the url
            IWasabiHttpClientFactory factory = null;
            if (uri.Scheme == "nostr" &&
                uri.AbsolutePath.FromNIP19Note() is NIP19.NosteProfileNote nostrProfileNote)
            {
                 factory = new NostrWabisabiClientFactory(null, nostrProfileNote);
                 
            }
            else
            {
                factory = new WasabiHttpClientFactory(null, () => uri);
            }

            try
            {

                if (factory is IHostedService hs)
                {
                    await hs.StartAsync(cancel);
                }
                var handler = factory.NewWabiSabiApiRequestHandler(Mode.DefaultCircuit);
               
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var resp = await handler.GetStatusAsync(RoundStateRequest.Empty,  cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not connect to the coordinator at {0}", uri);
                await Task.Delay(TimeSpan.FromMinutes(1), cancel);
                TriggerRound();
                return;
            }
            finally
            {
                if (factory is IHostedService hs)
                {
                    await hs.StopAsync(cancel);
                }
            }

          
            _logger.LogInformation("Publishing coordinator discovery event with url {0}", uri);
            
            
            await Nostr.Publish(s.NostrRelay,
                new[]
                {
                    await Nostr.CreateCoordinatorDiscoveryEvent(network, k, uri,
                        s.CoordinatorDescription)
                },s.UriToAdvertise.IsOnion()?  _socks5HttpClientHandler: null, cancel);
        }
    }
}
