using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments.PayJoin;
using BTCPayServer.Services;
using ExchangeSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NNostr.Client;
using NNostr.Client.Protocols;
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
using ClientWebSocket = System.Net.WebSockets.ClientWebSocket;

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
        // _walletProvider.WalletUnloaded += WalletProviderOnWalletUnloaded;
        // _walletProvider.Walleloaded += WalletProviderOnWalletloaded;
        //
    }

    // private void WalletProviderOnWalletUnloaded(object sender, WalletProvider.WalletUnloadEventArgs e)
    // {
    //     _ =StopWallet(e.Wallet);
    // }
    // private void WalletProviderOnWalletloaded(object sender, WalletProvider.WalletUnloadEventArgs e)
    // {
    //     _ =StartWallet(e.Wallet as BTCPayWallet);
    // }

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
        foreach (var coordinatorManager in HostedServices)
        {
            await coordinatorManager.Value.StopAsync(cancellationToken);
        }
    }

    // public async Task StopWallet(IWallet wallet, string coordinator = null)
    // {
    //     if (coordinator is not null && HostedServices.TryGetValue(coordinator, out var instance))
    //     {
    //         await instance.StopWallet(wallet);
    //     }
    //     else if (coordinator is null)
    //     {
    //         foreach (var servicesValue in HostedServices.Values)
    //         {
    //             await servicesValue.StopWallet(wallet);
    //         }
    //     }
    // }
    // public async Task StartWallet(BTCPayWallet wallet, string coordinator = null)
    // {
    //     if (coordinator is not null && HostedServices.TryGetValue(coordinator, out var instance))
    //     {
    //         await instance.StartWallet(wallet);
    //     }
    //     else if (coordinator is null)
    //     {
    //         foreach (var servicesValue in HostedServices.Values)
    //         {
    //             await servicesValue.StartWallet(wallet);
    //         }
    //     }
    // }

    
    public  void AddCoordinator(string displayName, string name,
        Func<IServiceProvider, Uri> fetcher, CoinJoinConfiguration configuration, string termsConditions = null, string description = null)
    {
        configuration ??= new CoinJoinConfiguration("CoinJoinCoordinatorIdentifier",150m,  1, false);

        if (termsConditions is null && name == "zksnacks")
        {
            try
            {
                termsConditions = new HttpClient().GetStringAsync("https://wasabiwallet.io/api/v4/Wasabi/legaldocuments?id=ww2")
                    .Result;
            }
            catch (Exception e)
            {
            }
           

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
        
        var coordinator = url is null ? null : new Uri(url);


        IWasabiHttpClientFactory wasabiHttpClientFactory;
        if (name == "local" || coordinator is null)
        {
          var controller =   _provider.GetService<WabiSabiController>();
          if(controller is null)
              return;
          wasabiHttpClientFactory = new LocalWabisabiClientFactory( controller);

        }
        else if (coordinator.Scheme == "nostr" &&
                coordinator.AbsolutePath.TrimEnd('/').FromNIP19Note() is NIP19.NosteProfileNote nostrProfileNote)
            {
                var socks5HttpClientHandler = _provider.GetRequiredService<Socks5HttpClientHandler>();
            
                var factory = new NostrWabisabiClientFactory(socks5HttpClientHandler, nostrProfileNote);
                wasabiHttpClientFactory = factory;
            }
        else
        {
            var config = _provider.GetService<IConfiguration>();
            var socksEndpoint = config.GetValue<string>("socksendpoint");
            EndPointParser.TryParse(socksEndpoint, 9050, out var torEndpoint);
            if (torEndpoint is not null && torEndpoint is DnsEndPoint dnsEndPoint)
            {
                torEndpoint = new IPEndPoint(Dns.GetHostAddresses(dnsEndPoint.Host).First(), dnsEndPoint.Port);
            }

            
            wasabiHttpClientFactory = new WasabiHttpClientFactory(torEndpoint, () => coordinator);
        }


        var instance = new WabisabiCoordinatorClientInstance(
            displayName,
            name, url is null? null: new Uri(url), wasabiHttpClientFactory,_provider.GetService<ILoggerFactory>(), _provider, UTXOLocker,
            _provider.GetService<WalletProvider>(), termsConditions, description, configuration);
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

public class NostrWabisabiClientFactory: IWasabiHttpClientFactory, IHostedService
{
    private readonly Socks5HttpClientHandler _socks5HttpClientHandler;
    private readonly NIP19.NosteProfileNote _nostrProfileNote;

    public NostrWabisabiClientFactory(Socks5HttpClientHandler socks5HttpClientHandler,
        NIP19.NosteProfileNote nostrProfileNote)
    {
        _socks5HttpClientHandler = socks5HttpClientHandler;
        _nostrProfileNote = nostrProfileNote;
    }

    private ConcurrentDictionary<string, NostrWabiSabiApiClient>  _clients = new();

    private bool _started = false;
    
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_clients.Select(pair => pair.Value.StartAsync(cancellationToken)));
        
        _started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var nostrWabiSabiApiClient in _clients)
        {
            nostrWabiSabiApiClient.Value.Dispose();
        }
        _clients.Clear();
        _started = false;
        return Task.CompletedTask;
    }

    public IWabiSabiApiRequestHandler NewWabiSabiApiRequestHandler(Mode mode, ICircuit circuit = null)
    {
        if (mode == Mode.DefaultCircuit || _socks5HttpClientHandler?.Proxy is null)
        {
            circuit = DefaultCircuit.Instance;
        }

        if (mode == Mode.NewCircuitPerRequest)
        {
            circuit = new OneOffCircuit();
        }
        
        if (circuit is not INamedCircuit namedCircuit)
            throw new ArgumentException("circuit must be a INamedCircuit");
        var result =  _clients.GetOrAdd(namedCircuit.Name, name =>
        {
            var result = new NostrWabiSabiApiClient(new Uri(_nostrProfileNote.Relays.First()),
                _socks5HttpClientHandler?.Proxy as WebProxy, NostrExtensions.ParsePubKey(_nostrProfileNote.PubKey),
                namedCircuit);
            if (_started)
            {
                
                result.StartAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            return result;
        });
        
        return result;
    }
}



public class LocalWabisabiClientFactory: IWasabiHttpClientFactory
{
    private readonly WabiSabiController _wabiSabiController;

    public LocalWabisabiClientFactory(WabiSabiController wabiSabiController)
    {
        _wabiSabiController = wabiSabiController;
    }
    public IWabiSabiApiRequestHandler NewWabiSabiApiRequestHandler(Mode mode, ICircuit circuit = null)
    {
        return _wabiSabiController;
    }
}

public class WabisabiCoordinatorClientInstance:IHostedService
{
    private readonly IUTXOLocker _utxoLocker;
    private readonly ILogger _logger;
    public string CoordinatorDisplayName { get; }
    public string CoordinatorName { get; set; }
    public Uri Coordinator { get; set; }
    public WalletProvider WalletProvider { get; }
    public string TermsConditions { get; set; }
    public IWasabiHttpClientFactory WasabiHttpClientFactory { get; set; }
    public RoundStateUpdater RoundStateUpdater { get; set; }
    public CoinPrison CoinPrison { get; private set; }
    public WasabiCoordinatorStatusFetcher WasabiCoordinatorStatusFetcher { get; set; }
    public CoinJoinManager CoinJoinManager { get; set; }
    public string Description { get; set; }
    public readonly WalletWasabi.Services.HostedServices _hostedServices = new();

    public WabisabiCoordinatorClientInstance(
        string coordinatorDisplayName,
        string coordinatorName,
        Uri coordinator,
        IWasabiHttpClientFactory wasabiHttpClientFactory,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IUTXOLocker utxoLocker,
        WalletProvider walletProvider, string termsConditions, string description, CoinJoinConfiguration config)
    {

        _utxoLocker = utxoLocker;
        
        CoordinatorDisplayName = coordinatorDisplayName;
        CoordinatorName = coordinatorName;
        Coordinator = coordinator;
        WalletProvider = walletProvider;
        TermsConditions = termsConditions;
        Description = description;
        _logger = loggerFactory.CreateLogger(coordinatorName);
        IWabiSabiApiRequestHandler sharedWabisabiClient = null;

        var roundStateUpdaterCircuit = new PersonCircuit();
        WasabiHttpClientFactory = wasabiHttpClientFactory;
        if(wasabiHttpClientFactory is IHostedService hostedService)
            _hostedServices.Register<IHostedService>(() => hostedService, hostedService.GetType().Name);
        
        
        sharedWabisabiClient =
            WasabiHttpClientFactory.NewWabiSabiApiRequestHandler(Mode.SingleCircuitPerLifetime,
                roundStateUpdaterCircuit);

        if (termsConditions is null && sharedWabisabiClient is WabiSabiHttpApiClient wabiSabiHttpApiClient)
        {

            _ = wabiSabiHttpApiClient.GetLegalDocumentsAsync(CancellationToken.None)
                .ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        TermsConditions = task.Result;
                    }
                });
        }

        WasabiCoordinatorStatusFetcher = new WasabiCoordinatorStatusFetcher(sharedWabisabiClient, _logger);

        RoundStateUpdater =
            new RoundStateUpdater(TimeSpan.FromSeconds(5), sharedWabisabiClient, WasabiCoordinatorStatusFetcher);

        CoinPrison = SettingsCoinPrison.CreateFromCoordinatorName(
            serviceProvider.GetRequiredService<SettingsRepository>(),
            CoordinatorName, _logger).GetAwaiter().GetResult();
        
        CoinJoinManager = new CoinJoinManager(coordinatorName, WalletProvider, RoundStateUpdater,
            WasabiHttpClientFactory,
            WasabiCoordinatorStatusFetcher, config, CoinPrison);
        CoinJoinManager.StatusChanged += OnStatusChanged;
        
        _hostedServices.Register<RoundStateUpdater>(() => RoundStateUpdater, "RoundStateUpdater");
        _hostedServices.Register<WasabiCoordinatorStatusFetcher>(() => WasabiCoordinatorStatusFetcher, "WasabiCoordinatorStatusFetcher");
        _hostedServices.Register<CoinJoinManager>(() => CoinJoinManager, "WasabiCoordinatorStatusFetcher");
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
            // case LoadedEventArgs loadedEventArgs:
            //     stopWhenAllMixed = !((BTCPayWallet)loadedEventArgs.Wallet).BatchPayments;
            //    _ = CoinJoinManager.StartAsync(loadedEventArgs.Wallet, stopWhenAllMixed, false, CancellationToken.None);
            //     break;
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
        _ = _hostedServices.StartAllAsync(cancellationToken);
       
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = _hostedServices.StopAllAsync(cancellationToken);
        return Task.CompletedTask;
    }
}
