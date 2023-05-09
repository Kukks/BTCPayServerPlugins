using System;
using System.IO;
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
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using Newtonsoft.Json.Linq;
using WalletWasabi.Bases;
using WalletWasabi.BitcoinCore.Rpc;
using WalletWasabi.Cache;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using WalletWasabi.WabiSabi.Backend.Statistics;

namespace WalletWasabi.Backend.Controllers;

public class WabisabiCoordinatorService : PeriodicRunner
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly IExplorerClientProvider _clientProvider;
    private readonly IMemoryCache _memoryCache;
    private readonly WabisabiCoordinatorClientInstanceManager _instanceManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LinkGenerator _linkGenerator;

    public readonly IdempotencyRequestCache IdempotencyRequestCache;

    public bool Started => HostedServices.IsStartAllAsyncStarted;
    private HostedServices HostedServices { get; } = new();
    public WabiSabiCoordinator WabiSabiCoordinator { get; private set; }

    public WabisabiCoordinatorService(ISettingsRepository settingsRepository,
        IOptions<DataDirectories> dataDirectories, IExplorerClientProvider clientProvider, IMemoryCache memoryCache,
        WabisabiCoordinatorClientInstanceManager instanceManager,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        LinkGenerator linkGenerator) : base(TimeSpan.FromMinutes(15))
    {
        _settingsRepository = settingsRepository;
        _dataDirectories = dataDirectories;
        _clientProvider = clientProvider;
        _memoryCache = memoryCache;
        _instanceManager = instanceManager;
        _httpClientFactory = httpClientFactory;
        _linkGenerator = linkGenerator;
        _socks5HttpClientHandler = serviceProvider.GetRequiredService<Socks5HttpClientHandler>();
        IdempotencyRequestCache = new(memoryCache);
    }
    

    private WabisabiCoordinatorSettings cachedSettings;
    private readonly Socks5HttpClientHandler _socks5HttpClientHandler;

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
        else if (existing.Enabled &&
                 _instanceManager.HostedServices.TryGetValue("local", out var instance))
        {
            instance.TermsConditions = wabisabiCoordinatorSettings.TermsConditions;
        }

        try
        {

            await this.ActionAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
        }
        await _settingsRepository.UpdateSetting(wabisabiCoordinatorSettings, nameof(WabisabiCoordinatorSettings));
    }

    public class BtcPayRpcClient : CachedRpcClient
    {
        private readonly ExplorerClient _explorerClient;

        public BtcPayRpcClient(RPCClient rpc, IMemoryCache cache, ExplorerClient explorerClient) : base(rpc, cache)
        {
            _explorerClient = explorerClient;
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

        public override async Task<EstimateSmartFeeResponse> EstimateSmartFeeAsync(int confirmationTarget,
            EstimateSmartFeeMode estimateMode = EstimateSmartFeeMode.Conservative,
            CancellationToken cancellationToken = default)
        {

            string cacheKey = $"{nameof(EstimateSmartFeeAsync)}:{confirmationTarget}:{estimateMode}";

            return await IdempotencyRequestCache.GetCachedResponseAsync(
                cacheKey,
                action: async (_, cancellationToken) =>
                {
                    var result = await _explorerClient.GetFeeRateAsync(confirmationTarget, new FeeRate(100m), cancellationToken);
                    return new EstimateSmartFeeResponse() {FeeRate = result.FeeRate, Blocks = result.BlockCount};
                },
                options: CacheOptionsWithExpirationToken(size: 1, expireInSeconds: 60),
                cancellationToken).ConfigureAwait(false);

        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var explorerClient = _clientProvider.GetExplorerClient("BTC");
        var coordinatorParameters =
            new CoordinatorParameters(Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "Coinjoin"));
        var coinJoinIdStore =
            CoinJoinIdStore.Create(
                Path.Combine(coordinatorParameters.ApplicationDataDir, "CcjCoordinator",
                    $"CoinJoins{explorerClient.Network}.txt"), coordinatorParameters.CoinJoinIdStoreFilePath);
        var coinJoinScriptStore = CoinJoinScriptStore.LoadFromFile(coordinatorParameters.CoinJoinScriptStoreFilePath);
        var rpc = new BtcPayRpcClient(explorerClient.RPCClient, _memoryCache, explorerClient);

        WabiSabiCoordinator = new WabiSabiCoordinator(coordinatorParameters, rpc, coinJoinIdStore, coinJoinScriptStore,
            _httpClientFactory);
        HostedServices.Register<WabiSabiCoordinator>(() => WabiSabiCoordinator, "WabiSabi Coordinator");
        var settings = await GetSettings();
        if (settings.Enabled is true)
        {
            _ = StartCoordinator(cancellationToken);
        }

        await base.StartAsync(cancellationToken);
    }

    public async Task StartCoordinator(CancellationToken cancellationToken)
    {
        await HostedServices.StartAllAsync(cancellationToken);
        if (_instanceManager.HostedServices.TryGetValue("local", out var instance))
        {
            instance.WasabiCoordinatorStatusFetcher.OverrideConnected = null;
        }
        _instanceManager.AddCoordinator("Local Coordinator", "local", _ => null, cachedSettings.TermsConditions);
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
        if (s.Enabled && !string.IsNullOrEmpty(s.NostrIdentity) && s.NostrRelay is not null &&
            s.UriToAdvertise is not null)
        {

            var uri = new Uri(s.UriToAdvertise, "plugins/wabisabi-coordinator/wabisabi");
            await Nostr.Publish(s.NostrRelay,
                new[]
                {
                    await Nostr.CreateCoordinatorDiscoveryEvent(network, s.NostrIdentity, uri,
                        s.CoordinatorDescription)
                },s.UriToAdvertise.IsOnion()?  _socks5HttpClientHandler: null, cancel);
        }
    }
}
