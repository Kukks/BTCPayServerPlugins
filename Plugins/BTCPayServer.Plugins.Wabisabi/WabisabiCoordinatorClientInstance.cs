using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalletWasabi.Backend.Controllers;
using WalletWasabi.Tor.Socks5.Pool.Circuits;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Banning;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.Wallets;
using WalletWasabi.WebClients.Wasabi;

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

    public async Task StopWallet(IWallet wallet, string coordinator = null)
    {
        if (coordinator is not null && HostedServices.TryGetValue(coordinator, out var instance))
        {
            await instance.StopWallet(wallet);
        }
        else if (coordinator is null)
        {
            foreach (var servicesValue in HostedServices.Values)
            {
                await servicesValue.StopWallet(wallet);
            }
        }
    }

    
    public  void AddCoordinator(string displayName, string name,
        Func<IServiceProvider, Uri> fetcher, string termsConditions = null, string description = null)
    {
        if (termsConditions is null && name == "zksnacks")
        {
            termsConditions = new HttpClient().GetStringAsync("https://wasabiwallet.io/api/v4/Wasabi/legaldocuments?id=ww2")
                .Result;

        }
        if (HostedServices.ContainsKey(name))
        {
            return;
        }
        
        
        var url = fetcher.Invoke(_provider)?.AbsoluteUri;
        if (url is not null)
        {
            url = url.EndsWith("/") is true
                ? url
                : url + "/";
        }
           
        var instance = new WabisabiCoordinatorClientInstance(
            displayName,
            name, url is null? null: new Uri(url), _provider.GetService<ILoggerFactory>(), _provider, UTXOLocker,
            _provider.GetService<WalletProvider>(), termsConditions, description);
        if (HostedServices.TryAdd(instance.CoordinatorName, instance))
        {
            if(started)
                _ = instance.StartAsync(CancellationToken.None);
            if(name == "local")
                instance.WasabiCoordinatorStatusFetcher.OverrideConnected = null;
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
    public WasabiHttpClientFactory WasabiHttpClientFactory { get; set; }
    public RoundStateUpdater RoundStateUpdater { get; set; }
    public CoinPrison CoinPrison { get; private set; }
    public WasabiCoordinatorStatusFetcher WasabiCoordinatorStatusFetcher { get; set; }
    public CoinJoinManager CoinJoinManager { get; set; }
    public string Description { get; set; }

    public WabisabiCoordinatorClientInstance(string coordinatorDisplayName,
        string coordinatorName,
        Uri coordinator,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IUTXOLocker utxoLocker,
        WalletProvider walletProvider, string termsConditions, string description,string coordinatorIdentifier = "CoinJoinCoordinatorIdentifier")
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
        Description = description;
        _logger = loggerFactory.CreateLogger(coordinatorName);
        IWabiSabiApiRequestHandler sharedWabisabiClient;
        if (coordinatorName == "local")
        {
            sharedWabisabiClient = serviceProvider.GetRequiredService<WabiSabiController>();
            
        }
        else
        {
            WasabiHttpClientFactory = new WasabiHttpClientFactory(torEndpoint, () => Coordinator);
            var roundStateUpdaterCircuit = new PersonCircuit();
            var roundStateUpdaterHttpClient =
                WasabiHttpClientFactory.NewHttpClient(Mode.SingleCircuitPerLifetime, roundStateUpdaterCircuit);
            if (termsConditions is null)
            {
                _ = new WasabiClient(roundStateUpdaterHttpClient)
                    .GetLegalDocumentsAsync(CancellationToken.None)
                    .ContinueWith(task =>
                    {
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            TermsConditions = task.Result;
                        }
                    });
            }
            sharedWabisabiClient = new WabiSabiHttpApiClient(roundStateUpdaterHttpClient);
           
        }
        
        WasabiCoordinatorStatusFetcher = new WasabiCoordinatorStatusFetcher(sharedWabisabiClient, _logger);
        
        RoundStateUpdater = new RoundStateUpdater(TimeSpan.FromSeconds(5),sharedWabisabiClient, WasabiCoordinatorStatusFetcher);

        CoinPrison = SettingsCoinPrison.CreateFromCoordinatorName(serviceProvider.GetRequiredService<SettingsRepository>(),
            CoordinatorName).GetAwaiter().GetResult();
        if (coordinatorName == "local")
        {
            CoinJoinManager = new CoinJoinManager(coordinatorName, WalletProvider, RoundStateUpdater,
                sharedWabisabiClient, null,
                WasabiCoordinatorStatusFetcher, coordinatorIdentifier, CoinPrison);
        }
        else
        {
            CoinJoinManager = new CoinJoinManager(coordinatorName,WalletProvider, RoundStateUpdater,null,  WasabiHttpClientFactory,
                WasabiCoordinatorStatusFetcher, coordinatorIdentifier, CoinPrison);
        }

        CoinJoinManager.StatusChanged += OnStatusChanged;
    }

    public async Task StopWallet(IWallet wallet)
    {
        await CoinJoinManager.StopAsync(wallet, CancellationToken.None);
    }

    private void OnStatusChanged(object sender, StatusChangedEventArgs e)
    {
        bool stopWhenAllMixed;
        switch (e)
        {
            case CoinJoinStatusEventArgs coinJoinStatusEventArgs:
                _logger.LogTrace(coinJoinStatusEventArgs.CoinJoinProgressEventArgs.GetType() + "   :" +
                                       e.Wallet.WalletName);
                break;
            case CompletedEventArgs completedEventArgs:
                
                var result = completedEventArgs.CoinJoinResult;
                
                if (completedEventArgs.CompletionStatus == CompletionStatus.Success && result is SuccessfulCoinJoinResult successfulCoinJoinResult)
                {
                    Task.Run(async () =>
                    {
                        
                        var wallet = (BTCPayWallet) e.Wallet;
                        await wallet.RegisterCoinjoinTransaction(successfulCoinJoinResult, CoordinatorName);
                                    
                    });
                }
                else if(result is DisruptedCoinJoinResult disruptedCoinJoinResult )
                {
                    Task.Run(async () =>
                    {
                        // _logger.LogInformation("unlocking coins because round failed");
                        await _utxoLocker.TryUnlock(
                            disruptedCoinJoinResult.SignedCoins.Select(coin => coin.Outpoint).ToArray());
                    });
                    break;
                }
                _logger.LogTrace("Coinjoin complete!   :" + e.Wallet.WalletName);
                break;
            case LoadedEventArgs loadedEventArgs:
                stopWhenAllMixed = !((BTCPayWallet)loadedEventArgs.Wallet).BatchPayments;
               _ = CoinJoinManager.StartAsync(loadedEventArgs.Wallet, stopWhenAllMixed, false, CancellationToken.None);
                break;
            case StartErrorEventArgs errorArgs:
                _logger.LogTrace("Could not start wallet for coinjoin:" + errorArgs.Error.ToString() + "   :" + e.Wallet.WalletName);
                break;
            case StoppedEventArgs stoppedEventArgs:
                _logger.LogInformation("Stopped wallet for coinjoin: " + stoppedEventArgs.Reason + "   :" + e.Wallet.WalletName);
                break;
            default:
                _logger.LogTrace(e.GetType() + "   :" + e.Wallet.WalletName);
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
