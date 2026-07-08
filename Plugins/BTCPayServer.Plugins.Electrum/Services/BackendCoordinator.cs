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
using NBXplorer;
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
    private readonly ConcurrentDictionary<string, HysteresisState> _hysteresis = new();
    private readonly ElectrumDbContextFactory _dbFactory;
    private readonly RealNbxGateway _realNbx;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IndexFastForwarder _fastForwarder;
    private readonly ReservedIndexLedger _reservedLedger;
    private readonly ILogger<BackendCoordinator> _logger;
    private CancellationTokenSource _cts;
    private Task _pollTask;

    // Dependencies are optional so this remains constructible with `new BackendCoordinator()`
    // in unit tests that only exercise the pure Get/Set/Decide surface. When resolved via DI
    // (ElectrumPlugin.cs), all six are registered singletons and always supplied.
    public BackendCoordinator(
        ElectrumDbContextFactory dbFactory = null,
        RealNbxGateway realNbx = null,
        BTCPayNetworkProvider networkProvider = null,
        IndexFastForwarder fastForwarder = null,
        ReservedIndexLedger reservedLedger = null,
        ILogger<BackendCoordinator> logger = null)
    {
        _dbFactory = dbFactory;
        _realNbx = realNbx;
        _networkProvider = networkProvider;
        _fastForwarder = fastForwarder;
        _reservedLedger = reservedLedger;
        _logger = logger;
    }

    // Per-wallet anti-flap bookkeeping consumed by HysteresisGate: how many consecutive
    // evaluations have agreed on the desired backend, and how many polls have elapsed
    // since the last actual flip (for the cooldown).
    private sealed class HysteresisState
    {
        public WalletBackend Desired;
        public int ConsecutiveAgree;
        public int PollsSinceFlip;
    }

    // Read-only per-wallet snapshot returned by SnapshotStates() for the admin status panel.
    public sealed record WalletBackendState(string WalletId, WalletBackend Active, int ConsecutiveAgree);

    public WalletBackend GetActiveBackend(string walletId) =>
        _active.TryGetValue(walletId, out var b) ? b : WalletBackend.Electrum;

    public void SetActiveBackend(string walletId, WalletBackend backend) =>
        _active[walletId] = backend;

    public IReadOnlyDictionary<string, WalletBackend> Snapshot() =>
        new Dictionary<string, WalletBackend>(_active);

    // Read-only copy of both bookkeeping dictionaries for the admin status panel (P4 Task 3).
    // Union of both key sets: a wallet can have an active backend without hysteresis history
    // yet (never evaluated), or vice versa is not expected but handled defensively anyway.
    public IReadOnlyList<WalletBackendState> SnapshotStates()
    {
        var walletIds = new HashSet<string>(_active.Keys);
        walletIds.UnionWith(_hysteresis.Keys);

        return walletIds
            .Select(id => new WalletBackendState(
                id,
                _active.TryGetValue(id, out var backend) ? backend : WalletBackend.Electrum,
                _hysteresis.TryGetValue(id, out var state) ? state.ConsecutiveAgree : 0))
            .ToList();
    }

    /// <summary>
    /// Pure readiness gate: NBX becomes authoritative for a wallet only when NBX's global
    /// sync is complete AND NBX is already tracking that specific wallet.
    /// </summary>
    public static WalletBackend DecideBackend(bool nbxSynced, bool trackedInNbx) =>
        nbxSynced && trackedInNbx ? WalletBackend.Nbx : WalletBackend.Electrum;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_dbFactory == null || _realNbx == null || _networkProvider == null || _fastForwarder == null || _reservedLedger == null)
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
        try
        {
            await SeedReservedLedgerFromNbxAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Startup ledger seed from NBX failed");
        }

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

        foreach (var walletId in walletIds)
        {
            await EvaluateWalletAsync(walletId, ct);
        }
    }

    /// <summary>
    /// Evaluates and (if needed) flips the active backend for a single wallet. This holds the
    /// same per-wallet decision PollOnceAsync runs for every tracked wallet every 30s, extracted
    /// so callers that just tracked a wallet (ElectrumHttpHandler's Track/GenerateWallet mirrors)
    /// can trigger it immediately instead of waiting for the next poll. That shrinks — but does
    /// not fully eliminate — the race window where a newly tracked wallet sits on the stale
    /// default backend while both ElectrumListener and the ungated NBXplorerListener are able to
    /// publish for it; a notification that lands within this evaluation's own latency can still
    /// race. Full elimination is P4 reconciliation.
    /// </summary>
    public async Task EvaluateWalletAsync(string walletId, CancellationToken ct)
    {
        if (_realNbx == null || _networkProvider == null || _fastForwarder == null)
            return; // not wired (e.g. constructed directly in a test)

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

        var trackedInNbx = false;
        try
        {
            var strategy = new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(walletId);
            var trackedSource = TrackedSource.Create(strategy);
            trackedInNbx = await nbx.IsTrackedAsync(trackedSource, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to check NBX tracking for wallet {WalletId}", walletId);
        }

        var desired = DecideBackend(nbxSynced, trackedInNbx);
        var currentBackend = GetActiveBackend(walletId);

        var state = _hysteresis.GetOrAdd(walletId, _ => new HysteresisState());
        if (desired == state.Desired)
        {
            state.ConsecutiveAgree++;
        }
        else
        {
            state.Desired = desired;
            state.ConsecutiveAgree = 1;
        }
        state.PollsSinceFlip++;

        var cooldownElapsed = state.PollsSinceFlip >= HysteresisGate.CooldownPolls;
        if (!HysteresisGate.ShouldFlip(currentBackend, desired, state.ConsecutiveAgree, HysteresisGate.RequiredFor(desired), cooldownElapsed))
            return; // not stable/cooled-down enough yet — leave the active backend as-is, retry next poll

        // Only fast-forward on an actual transition — fast-forwarding every poll
        // (even when the backend hasn't changed) would burn addresses for no reason.
        try
        {
            await _fastForwarder.FastForwardAsync(walletId, desired, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Fast-forward failed for wallet {WalletId} transitioning to {Backend}; skipping flip this poll", walletId, desired);
            return; // don't flip if we couldn't clear the high-water first
        }

        SetActiveBackend(walletId, desired);
        state.PollsSinceFlip = 0;
    }

    // NBXplorer exposes no "list all tracked derivation schemes" API, so a true NBX -> Electrum
    // discovery of wallets Electrum has never seen is not possible. We can only seed the ledger
    // for wallets already present in the Electrum DB (TrackedWallets) — this reconciles those
    // wallets' reserved-index high-water with whatever NBX already handed out, so Electrum
    // starts life as a no-reuse standby for them. Wallets NBX tracks but Electrum has never
    // seen reach both backends when BTCPay re-tracks each store's derivation on startup, which
    // flows through the handler's mirror-Track.
    private async Task SeedReservedLedgerFromNbxAsync(CancellationToken ct)
    {
        List<TrackedWallet> wallets;
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            wallets = await ctx.TrackedWallets.ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load tracked wallets for startup ledger seed");
            return;
        }

        foreach (var wallet in wallets)
        {
            var nbx = _realNbx.GetClient(wallet.CryptoCode);
            if (nbx == null)
                continue;

            var network = _networkProvider.GetNetwork<BTCPayNetwork>(wallet.CryptoCode);
            if (network == null)
                continue;

            DerivationStrategyBase strategy;
            try
            {
                strategy = new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(wallet.DerivationStrategy);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to parse derivation strategy for wallet {WalletId} during ledger seed", wallet.Id);
                continue;
            }

            foreach (var isChange in new[] { false, true })
            {
                var feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit;
                try
                {
                    var info = await nbx.GetUnusedAsync(strategy, feature, 0, false, ct);
                    var index = info?.Index ?? GetLastKeyPathIndex(info);
                    if (index is not { } nextIndex)
                        continue; // NBX has no record for this wallet/feature yet

                    if (nextIndex > 0)
                        await _reservedLedger.RecordReserveAsync(wallet.Id, isChange, nextIndex - 1, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to read NBX next index for wallet {WalletId} feature {Feature} during ledger seed", wallet.Id, feature);
                }
            }
        }
    }

    private static int? GetLastKeyPathIndex(KeyPathInformation info) =>
        info?.KeyPath?.Indexes is { Length: > 0 } indexes ? (int)indexes[^1] : null;
}
