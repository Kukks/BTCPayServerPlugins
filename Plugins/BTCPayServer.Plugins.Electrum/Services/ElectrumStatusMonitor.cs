using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Electrum.Services;

/// <summary>
/// Replaces NBXplorerWaiters. Monitors the Electrum server connection status
/// and publishes state changes via EventAggregator.
/// </summary>
public class ElectrumStatusMonitor : IHostedService
{
    private readonly ElectrumClient _client;
    private readonly NBXplorerDashboard _dashboard;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly EventAggregator _eventAggregator;
    private readonly SettingsRepository _settingsRepository;
    private readonly RealNbxGateway _realNbxGateway;
    private readonly NbxHealth _nbxHealth;
    private readonly ILogger<ElectrumStatusMonitor> _logger;
    private CancellationTokenSource _cts;
    private Task _monitorLoop;

    public NBXplorerState State { get; private set; } = NBXplorerState.NotConnected;
    public int TipHeight { get; private set; }
    public string ServerVersion { get; private set; }
    public string ConnectedServer => _client.ConnectedServer;
    public string ConfiguredServer { get; private set; }

    /// <summary>
    /// BTC is effectively available if either backend can serve it: real NBX is
    /// fully synced, or the Electrum connection is up. This closes the window
    /// where NBX is still syncing but Electrum is already ready to serve.
    /// </summary>
    public static bool EffectiveSynced(bool nbxSynced, bool electrumConnected) =>
        nbxSynced || electrumConnected;

    public ElectrumStatusMonitor(
        ElectrumClient client,
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider,
        EventAggregator eventAggregator,
        SettingsRepository settingsRepository,
        RealNbxGateway realNbxGateway,
        NbxHealth nbxHealth,
        ILogger<ElectrumStatusMonitor> logger)
    {
        _client = client;
        _dashboard = dashboard;
        _networkProvider = networkProvider;
        _eventAggregator = eventAggregator;
        _settingsRepository = settingsRepository;
        _realNbxGateway = realNbxGateway;
        _nbxHealth = nbxHealth;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorLoop = MonitorLoop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_monitorLoop != null)
        {
            try { await _monitorLoop; } catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Electrum status monitor");
            }

            var delay = State == NBXplorerState.Ready ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(10);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task StepAsync(CancellationToken ct)
    {
        var oldState = State;
        var settings = await _settingsRepository.GetSettingAsync<ElectrumSettings>();
        if (settings == null)
        {
            settings = new ElectrumSettings();
            await _settingsRepository.UpdateSetting(settings);
        }
        ConfiguredServer = settings.Server;

        if (!_client.IsConnected)
        {
            try
            {
                await _client.ConnectAsync(ct);
                var (sw, pv) = await _client.ServerVersionAsync("BTCPayServer-Electrum", "1.4", ct);
                ServerVersion = sw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Cannot connect to Electrum server");
            }
        }

        var electrumHealthy = false;
        if (_client.IsConnected)
        {
            try
            {
                await _client.PingAsync(ct);
                await _client.ServerFeaturesAsync(ct);
                electrumHealthy = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Electrum server health check failed");
            }
        }

        // Effective readiness: BTC is available if either backend can serve it,
        // so we don't report "not synced" while NBX is catching up but Electrum
        // is already connected (and vice versa). Use the actual health-check result,
        // not the raw IsConnected flag — on a half-open connection IsConnected can stay
        // stale-true after a failed ping/ServerFeatures call.
        var nbxSynced = await QueryNbxSyncedAsync(ct);
        State = EffectiveSynced(nbxSynced, electrumHealthy) ? NBXplorerState.Ready : NBXplorerState.NotConnected;

        var status = BuildStatusResult();
        PublishDashboard(status, oldState);
    }

    private async Task<bool> QueryNbxSyncedAsync(CancellationToken ct)
    {
        var client = _realNbxGateway.GetClient("BTC");
        if (client == null)
            return false;

        try
        {
            var status = await client.GetStatusAsync(ct);
            _nbxHealth.Record(true);
            return status?.IsFullySynched is true;
        }
        catch (Exception ex)
        {
            _nbxHealth.Record(false);
            _logger.LogDebug(ex, "Failed to query real NBX status");
            return false;
        }
    }

    private StatusResult BuildStatusResult()
    {
        return new StatusResult
        {
            IsFullySynched = State == NBXplorerState.Ready,
            ChainHeight = TipHeight,
            SyncHeight = TipHeight,
            Version = ServerVersion ?? "electrum-plugin",
            SupportedCryptoCodes = new[] { "BTC" },
            NetworkType = GetChainName(),
            BitcoinStatus = new BitcoinStatus
            {
                Blocks = TipHeight,
                Headers = TipHeight,
                VerificationProgress = 1.0,
                IsSynched = State == NBXplorerState.Ready,
                MinRelayTxFee = new NBitcoin.FeeRate(1.0m),
                IncrementalRelayFee = new NBitcoin.FeeRate(1.0m),
                Capabilities = new NodeCapabilities
                {
                    CanScanTxoutSet = true
                }
            }
        };
    }

    private NBitcoin.ChainName GetChainName()
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        return network?.NBitcoinNetwork?.ChainName ?? NBitcoin.ChainName.Mainnet;
    }

    private void PublishDashboard(StatusResult status, NBXplorerState oldState)
    {
        foreach (var network in _networkProvider.GetAll().OfType<BTCPayNetwork>())
        {
            _dashboard.Publish(network, State, status, null, State == NBXplorerState.NotConnected ? "Electrum server not connected" : null);

            if (oldState != State)
            {
                _eventAggregator.Publish(new NBXplorerStateChangedEvent(network, oldState, State));
            }
        }
    }

    internal void UpdateTipHeight(int height)
    {
        TipHeight = height;
    }
}
