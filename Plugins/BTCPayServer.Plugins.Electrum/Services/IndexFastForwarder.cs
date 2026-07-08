#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Electrum.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace BTCPayServer.Plugins.Electrum.Services;

// On a backend takeover, the newly-active backend must not hand out an address
// the OTHER backend already reserved while it was active. ReservedIndexLedger
// (Task 2) tracks the cross-backend high-water per wallet+feature; this fast-
// forwards whichever backend is taking over past that high-water by burning
// (marking used) the addresses at/below it, for both Deposit and Change.
public class IndexFastForwarder
{
    // Buffer beyond the high-water on top of the iteration cap, guarding against
    // an endless loop if the backend's "next unused" index doesn't advance for
    // some reason (e.g. a bug upstream, or a stale/incorrect high-water).
    private const int IterationCapPadding = 25;

    private readonly ReservedIndexLedger _reservedLedger;
    private readonly RealNbxGateway _realNbx;
    private readonly ElectrumDbContextFactory _dbFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ElectrumWalletTracker _walletTracker;
    private readonly ILogger<IndexFastForwarder> _logger;

    public IndexFastForwarder(
        ReservedIndexLedger reservedLedger,
        RealNbxGateway realNbx,
        ElectrumDbContextFactory dbFactory,
        BTCPayNetworkProvider networkProvider,
        ElectrumWalletTracker walletTracker,
        ILogger<IndexFastForwarder> logger)
    {
        _reservedLedger = reservedLedger;
        _realNbx = realNbx;
        _dbFactory = dbFactory;
        _networkProvider = networkProvider;
        _walletTracker = walletTracker;
        _logger = logger;
    }

    // Pure rule: the incoming backend must be advanced past the reserved high-water.
    // highWater < 0 means nothing has been reserved yet, so there's nothing to burn.
    public static bool NeedsBurn(int backendNextIndex, int highWater) =>
        highWater >= 0 && backendNextIndex <= highWater;

    public async Task FastForwardAsync(string walletId, WalletBackend target, CancellationToken ct)
    {
        foreach (var isChange in new[] { false, true })
        {
            var highWater = await _reservedLedger.GetReservedAsync(walletId, isChange, ct);
            if (highWater < 0)
                continue; // nothing reserved for this feature yet, nothing to burn

            if (target == WalletBackend.Nbx)
                await FastForwardNbxAsync(walletId, isChange, highWater, ct);
            else
                await FastForwardElectrumAsync(walletId, isChange, highWater, ct);
        }
    }

    private async Task FastForwardNbxAsync(string walletId, bool isChange, int highWater, CancellationToken ct)
    {
        await using var ctx = _dbFactory.CreateContext();
        var wallet = await ctx.TrackedWallets.FindAsync(new object[] { walletId }, ct);
        if (wallet == null)
            return;

        var nbx = _realNbx.GetClient(wallet.CryptoCode);
        if (nbx == null)
        {
            _logger?.LogWarning("No NBX client configured for {CryptoCode}; cannot fast-forward wallet {WalletId}", wallet.CryptoCode, walletId);
            return;
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(wallet.CryptoCode);
        if (network == null)
            return;

        DerivationStrategyBase strategy;
        try
        {
            strategy = new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(wallet.DerivationStrategy);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse derivation strategy for wallet {WalletId}; skipping NBX fast-forward", walletId);
            return;
        }

        var feature = isChange ? DerivationFeature.Change : DerivationFeature.Deposit;
        var cap = highWater + IterationCapPadding + 1;

        for (var i = 0; i < cap; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Peek the next unused index WITHOUT reserving, so we don't burn the first index
            // above the high-water just to discover it didn't need burning — GetUnusedAsync with
            // reserve:true claims the address it returns, which would skip one extra index per
            // takeover.
            KeyPathInformation? peek;
            try
            {
                peek = await nbx.GetUnusedAsync(strategy, feature, 0, false, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read next unused NBX address for wallet {WalletId} feature {Feature}", walletId, feature);
                return;
            }

            var index = peek?.Index ?? GetLastKeyPathIndex(peek);
            if (index == null)
            {
                _logger?.LogWarning("NBX returned no index while burning wallet {WalletId} feature {Feature}; stopping", walletId, feature);
                return;
            }

            if (!NeedsBurn(index.Value, highWater))
                return; // already past the high-water — nothing more to burn, and don't reserve it

            // Still at/below the high-water: reserve (burn) this index so NBX advances past it.
            try
            {
                await nbx.GetUnusedAsync(strategy, feature, 0, true, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to burn NBX address for wallet {WalletId} feature {Feature}", walletId, feature);
                return;
            }

            _logger?.LogInformation(
                "Burned NBX address at index {Index} for wallet {WalletId} feature {Feature} (high-water {HighWater})",
                index, walletId, feature, highWater);
        }

        _logger?.LogWarning(
            "NBX fast-forward for wallet {WalletId} feature {Feature} hit the iteration cap without clearing high-water {HighWater}",
            walletId, feature, highWater);
    }

    private static int? GetLastKeyPathIndex(KeyPathInformation? info) =>
        info?.KeyPath?.Indexes is { Length: > 0 } indexes ? (int)indexes[^1] : null;

    private async Task FastForwardElectrumAsync(string walletId, bool isChange, int highWater, CancellationToken ct)
    {
        // Derive+persist any indices Electrum never locally derived (e.g. NBX handed
        // them out while Electrum was inactive) up to the high-water, then burn
        // everything at/below it. Doing the derive first closes the gap so
        // GetNextUnusedAddressAsync can't later derive-on-demand into an index
        // NBX already reserved.
        await _walletTracker.FastForwardToIndexAsync(walletId, isChange, highWater, ct);

        _logger?.LogInformation(
            "Fast-forwarded Electrum addresses for wallet {WalletId} feature {Feature} up to high-water {HighWater}",
            walletId, isChange ? "Change" : "Deposit", highWater);
    }
}
