using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Electrum.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Electrum.Services;

// Decides, per tracked wallet, which backend (Electrum or real NBX) is authoritative
// for reads/writes. P1's gate is intentionally simple: a wallet only moves to Nbx once
// NBX is globally synced AND already tracking that wallet. There is no failback,
// hysteresis, or stricter reconciliation here yet — those land in later phases.
public class BackendCoordinator : IHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const string CryptoCode = "BTC";

    private readonly ConcurrentDictionary<string, WalletBackend> _active = new();
    private readonly ElectrumDbContextFactory _dbFactory;
    private readonly RealNbxGateway _realNbx;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<BackendCoordinator> _logger;
    private CancellationTokenSource _cts;
    private Task _pollTask;

    // Dependencies are optional so this remains constructible with `new BackendCoordinator()`
    // in unit tests that only exercise the pure Get/Set/Decide surface. When resolved via DI
    // (ElectrumPlugin.cs), all four are registered singletons and always supplied.
    public BackendCoordinator(
        ElectrumDbContextFactory dbFactory = null,
        RealNbxGateway realNbx = null,
        BTCPayNetworkProvider networkProvider = null,
        ILogger<BackendCoordinator> logger = null)
    {
        _dbFactory = dbFactory;
        _realNbx = realNbx;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public WalletBackend GetActiveBackend(string walletId) =>
        _active.TryGetValue(walletId, out var b) ? b : WalletBackend.Electrum;

    public void SetActiveBackend(string walletId, WalletBackend backend) =>
        _active[walletId] = backend;

    public IReadOnlyDictionary<string, WalletBackend> Snapshot() =>
        new Dictionary<string, WalletBackend>(_active);

    /// <summary>
    /// Pure readiness gate: NBX becomes authoritative for a wallet only when NBX's global
    /// sync is complete AND NBX is already tracking that specific wallet.
    /// </summary>
    public static WalletBackend DecideBackend(bool nbxSynced, bool trackedInNbx) =>
        nbxSynced && trackedInNbx ? WalletBackend.Nbx : WalletBackend.Electrum;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_dbFactory == null || _realNbx == null || _networkProvider == null)
            return Task.CompletedTask; // not wired for polling (e.g. constructed directly in a test)

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_pollTask != null)
        {
            try
            {
                await _pollTask;
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Backend readiness poll failed");
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var nbx = _realNbx.GetClient(CryptoCode);
        if (nbx == null)
            return; // no NBX configured for this crypto code — stay on Electrum

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(CryptoCode);
        if (network == null)
            return;

        var nbxSynced = false;
        try
        {
            var status = await nbx.GetStatusAsync(ct);
            nbxSynced = status?.IsFullySynched ?? false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to read NBX status; treating as not synced");
        }

        List<string> walletIds;
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            walletIds = await ctx.TrackedWallets.Select(w => w.Id).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load tracked wallets for readiness poll");
            return;
        }

        var derivationFactory = new DerivationStrategyFactory(network.NBitcoinNetwork);

        foreach (var walletId in walletIds)
        {
            var trackedInNbx = false;
            try
            {
                var strategy = derivationFactory.Parse(walletId);
                var trackedSource = TrackedSource.Create(strategy);
                trackedInNbx = await nbx.IsTrackedAsync(trackedSource, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to check NBX tracking for wallet {WalletId}", walletId);
            }

            SetActiveBackend(walletId, DecideBackend(nbxSynced, trackedInNbx));
        }
    }
}
