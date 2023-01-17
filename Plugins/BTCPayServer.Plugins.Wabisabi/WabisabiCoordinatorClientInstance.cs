using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments.PayJoin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Services;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WebClients.Wasabi;
using HttpClientFactory = WalletWasabi.WebClients.Wasabi.HttpClientFactory;

namespace BTCPayServer.Plugins.Wabisabi;

public class WabisabiCoordinatorClientInstanceManager:IHostedService
{
    private readonly IServiceProvider _provider;
    private readonly WalletProvider _walletProvider;
    public Dictionary<string, WabisabiCoordinatorClientInstance> HostedServices { get; set; } = new();
    
    public WabisabiCoordinatorClientInstanceManager(IServiceProvider provider, WalletProvider walletProvider )
    {
        _provider = provider;
        _walletProvider = walletProvider;
        _walletProvider.WalletUnloaded += WalletProviderOnWalletUnloaded;
        
    }

    private void WalletProviderOnWalletUnloaded(object sender, WalletProvider.WalletUnloadEventArgs e)
    {
        _ =StopWallet(e.Wallet);
    }

    private bool started = false;
    public LocalisedUTXOLocker UTXOLocker;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        started = true;
        foreach (KeyValuePair<string,WabisabiCoordinatorClientInstance> coordinatorManager in HostedServices)
        {
            await coordinatorManager.Value.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string,WabisabiCoordinatorClientInstance> coordinatorManager in HostedServices)
        {
            await coordinatorManager.Value.StopAsync(cancellationToken);
        }
    }

    public async Task StopWallet(string name)
    {
        foreach (var servicesValue in HostedServices.Values)
        {
            await servicesValue.StopWallet(name);
        }
    }

    
    public  void AddCoordinator(string displayName, string name,
        Func<IServiceProvider, Uri> fetcher, string termsConditions = null)
    {
        if (termsConditions is null && name == "zksnacks")
        {
            termsConditions = new HttpClient().GetStringAsync("https://wasabiwallet.io/api/v4/Wasabi/legaldocuments")
                .Result;

        }
        if (HostedServices.ContainsKey(name))
        {
            return;
        }
        var instance = new WabisabiCoordinatorClientInstance(
            displayName,
            name, fetcher.Invoke(_provider), _provider.GetService<ILoggerFactory>(), _provider, UTXOLocker,
            _provider.GetService<WalletProvider>(), termsConditions);
        if (HostedServices.TryAdd(instance.CoordinatorName, instance))
        {
            if(started)
                _ = instance.StartAsync(CancellationToken.None);
        }
    }

    public async Task RemoveCoordinator(string name)
    {
        if (!HostedServices.TryGetValue(name, out var s))
        {
            return;
        }

        await s.StopAsync(CancellationToken.None);
        HostedServices.Remove(name);
    }
}

public class WabisabiCoordinatorClientInstance
{
    private readonly IUTXOLocker _utxoLocker;
    private readonly ILogger _logger;
    public string CoordinatorDisplayName { get; }
    public string CoordinatorName { get; set; }
    public Uri Coordinator { get; set; }
    public WalletProvider WalletProvider { get; }
    public string TermsConditions { get; set; }
    public HttpClientFactory WasabiHttpClientFactory { get; set; }
    public RoundStateUpdater RoundStateUpdater { get; set; }
    public WasabiCoordinatorStatusFetcher WasabiCoordinatorStatusFetcher { get; set; }
    public CoinJoinManager CoinJoinManager { get; set; }

    public WabisabiCoordinatorClientInstance(string coordinatorDisplayName,
        string coordinatorName,
        Uri coordinator,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IUTXOLocker utxoLocker,
        WalletProvider walletProvider, string termsConditions, string coordinatorIdentifier = "CoinJoinCoordinatorIdentifier")
    {
        
        _utxoLocker = utxoLocker;
        var config = serviceProvider.GetService<IConfiguration>();
        var socksEndpoint = config.GetValue<string>("socksendpoint");
        EndPointParser.TryParse(socksEndpoint,9050, out var torEndpoint);
        if (torEndpoint is not null && torEndpoint is DnsEndPoint dnsEndPoint)
        {
            torEndpoint = new IPEndPoint(Dns.GetHostAddresses(dnsEndPoint.Host).First(), dnsEndPoint.Port);
        }
        CoordinatorDisplayName = coordinatorDisplayName;
        CoordinatorName = coordinatorName;
        Coordinator = coordinator;
        WalletProvider = walletProvider;
        TermsConditions = termsConditions;
        _logger = loggerFactory.CreateLogger(coordinatorName);
        IWabiSabiApiRequestHandler sharedWabisabiClient;
        if (coordinatorName == "local")
        {
            sharedWabisabiClient = serviceProvider.GetRequiredService<WabiSabiController>();
            
        }
        else
        {
            WasabiHttpClientFactory = new HttpClientFactory(torEndpoint, () => Coordinator);
            var roundStateUpdaterCircuit = new PersonCircuit();
            var roundStateUpdaterHttpClient =
                WasabiHttpClientFactory.NewHttpClient(Mode.SingleCircuitPerLifetime, roundStateUpdaterCircuit);
            sharedWabisabiClient = new WabiSabiHttpApiClient(roundStateUpdaterHttpClient);
            CoinJoinManager = new CoinJoinManager(coordinatorName,WalletProvider, RoundStateUpdater, WasabiHttpClientFactory,
                WasabiCoordinatorStatusFetcher, coordinatorIdentifier);
        }
        
        WasabiCoordinatorStatusFetcher = new WasabiCoordinatorStatusFetcher(sharedWabisabiClient, _logger);
        
        RoundStateUpdater = new RoundStateUpdater(TimeSpan.FromSeconds(5),sharedWabisabiClient, WasabiCoordinatorStatusFetcher);
        if (coordinatorName == "local")
        {
            CoinJoinManager = new CoinJoinManager(coordinatorName, WalletProvider, RoundStateUpdater,
                sharedWabisabiClient,
                WasabiCoordinatorStatusFetcher, coordinatorIdentifier);
        }
        else
        {
            
            CoinJoinManager = new CoinJoinManager(coordinatorName, WalletProvider, RoundStateUpdater,
                WasabiHttpClientFactory,
                WasabiCoordinatorStatusFetcher, coordinatorIdentifier);
        }

        CoinJoinManager.StatusChanged += OnStatusChanged;
        CoinJoinManager.OnBan += (sender, args) =>
        {
            WalletProvider.OnBan(coordinatorName, args);
        };

    }

    public async Task StopWallet(string walletName)
    {
        await CoinJoinManager.StopAsyncByName(walletName, CancellationToken.None);
    }

    private void OnStatusChanged(object sender, StatusChangedEventArgs e)
    {
        
        switch (e)
        {
            case CoinJoinStatusEventArgs coinJoinStatusEventArgs:
                _logger.LogInformation(coinJoinStatusEventArgs.CoinJoinProgressEventArgs.GetType().ToString() + "   :" +
                                       e.Wallet.WalletName);
                break;
            case CompletedEventArgs completedEventArgs:
                
                var result = completedEventArgs.CoinJoinResult;
                
                if (completedEventArgs.CompletionStatus == CompletionStatus.Success)
                {
                    Task.Run(async () =>
                    {
                        
                        var wallet = (BTCPayWallet) e.Wallet;
                        await wallet.RegisterCoinjoinTransaction(result, CoordinatorName);
                                    
                    });
                }
                else
                {
                    Task.Run(async () =>
                    {
                        // _logger.LogInformation("unlocking coins because round failed");
                        await _utxoLocker.TryUnlock(
                            result.RegisteredCoins.Select(coin => coin.Outpoint).ToArray());
                    });
                    break;
                }
                _logger.LogInformation("Coinjoin complete!   :" +
                                       e.Wallet.WalletName);
                break;
            case LoadedEventArgs loadedEventArgs:
                var stopWhenAllMixed = !((BTCPayWallet)loadedEventArgs.Wallet).BatchPayments;
               _ = CoinJoinManager.StartAsync(loadedEventArgs.Wallet, stopWhenAllMixed, false, CancellationToken.None);
                _logger.LogInformation( "Loaded wallet  :" + e.Wallet.WalletName + $"stopWhenAllMixed: {stopWhenAllMixed}");
                break;
            case StartErrorEventArgs errorArgs:
                _logger.LogInformation("Could not start wallet for coinjoin:" + errorArgs.Error.ToString() + "   :" + e.Wallet.WalletName);
                break;
            case StoppedEventArgs stoppedEventArgs:
                _logger.LogInformation("Stopped wallet for coinjoin: " + stoppedEventArgs.Reason + "   :" + e.Wallet.WalletName);
                break;
            default:
                _logger.LogInformation(e.GetType() + "   :" + e.Wallet.WalletName);
                break;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        
        RoundStateUpdater.StartAsync(cancellationToken);
        WasabiCoordinatorStatusFetcher.StartAsync(cancellationToken);
        CoinJoinManager.StartAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        RoundStateUpdater.StopAsync(cancellationToken);
        WasabiCoordinatorStatusFetcher.StopAsync(cancellationToken);
        CoinJoinManager.StopAsync(cancellationToken);
        return Task.CompletedTask;
    }
}
