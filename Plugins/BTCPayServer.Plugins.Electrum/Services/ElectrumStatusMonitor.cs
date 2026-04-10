using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
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
    private readonly ILogger<ElectrumStatusMonitor> _logger;
    private CancellationTokenSource _cts;
    private Task _monitorLoop;

    public NBXplorerState State { get; private set; } = NBXplorerState.NotConnected;
    public int TipHeight { get; private set; }
    public string ServerVersion { get; private set; }

    public ElectrumStatusMonitor(
        ElectrumClient client,
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider,
        EventAggregator eventAggregator,
        ILogger<ElectrumStatusMonitor> logger)
    {
        _client = client;
        _dashboard = dashboard;
        _networkProvider = networkProvider;
        _eventAggregator = eventAggregator;
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
                SetState(NBXplorerState.NotConnected, oldState, null, null);
                return;
            }
        }

        try
        {
            await _client.PingAsync(ct);

            var features = await _client.ServerFeaturesAsync(ct);

            // We consider ourselves synced if connected and responding
            State = NBXplorerState.Ready;

            var status = BuildStatusResult();
            PublishDashboard(status, oldState);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Electrum server health check failed");
            State = NBXplorerState.NotConnected;
            SetState(NBXplorerState.NotConnected, oldState, null, null);
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

    private void SetState(NBXplorerState newState, NBXplorerState oldState, StatusResult status, GetMempoolInfoResponse mempoolInfo)
    {
        State = newState;
        PublishDashboard(status, oldState);
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
