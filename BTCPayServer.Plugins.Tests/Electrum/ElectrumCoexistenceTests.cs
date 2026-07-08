using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Electrum;
using NBXplorer.DerivationStrategy;
using BTCPayServer.Plugins.Electrum.Controllers;
using BTCPayServer.Plugins.Electrum.Services;
using BTCPayServer.Events;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NBitcoin;
using Npgsql;
using Xunit;

namespace BTCPayServer.Tests.Electrum;

// P4 Task 4: cross-phase integration batch (runtime verification) for the Electrum <-> NBX
// coexistence feature. This is the first time the ~23 commits of coexistence code (P1-P4)
// actually run inside a booted BTCPayServer host.
//
// Regtest reality (see ElectrumCoexistenceTestsBase and task brief): ElectrumClient's
// TrustedServers are all public MAINNET Electrum servers, so under ServerTester (regtest)
// the Electrum client can NEVER connect. Electrum is therefore permanently "down" here.
// That means:
//   - Everything that only needs NBX (host boot + DI composition, the Electrum DB migration
//     triggered by wallet tracking, wallet creation/balance/payment detection via NBX, and
//     the coordinator's Electrum -> Nbx flip) IS exercised and verified below.
//   - Everything that requires Electrum to actually be reachable (a wallet actually SERVED
//     by Electrum, a full round-trip flip back to Electrum with no-address-reuse, NBX-down
//     failback to Electrum, dashboard-up-via-Electrum, and the one-poll-NBX-blip hysteresis
//     scenario, which needs a live alternate backend to not flip to) is NOT exercised — those
//     need a regtest Electrum server (Fulcrum), which this stack does not provide. They are
//     stubbed below as explicit Skip facts rather than faked.
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class ElectrumCoexistenceTests : ElectrumCoexistenceTestsBase
{
    public ElectrumCoexistenceTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact(Timeout = 120_000)]
    [Trait("Integration", "Integration")]
    public async Task Boot_WithCoexistenceWiring_HostStartsAndComposesDi()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();

        // ElectrumPlugin.Execute only registers these when it actually ran (mainnet, or the
        // BTCPAY_ELECTRUM_ALLOWNONMAINNET escape hatch). Resolving them proves the plugin
        // loaded into this host (via DEBUG_PLUGINS) and its DI composition (removing
        // NBXplorer's own services, registering the Electrum engine + shadow services)
        // didn't crash the host at startup.
        var coordinator = tester.PayTester.GetService<BackendCoordinator>();
        Assert.NotNull(coordinator);
        var statusMonitor = tester.PayTester.GetService<ElectrumStatusMonitor>();
        Assert.NotNull(statusMonitor);
    }

    [Fact(Timeout = 280_000)]
    [Trait("Integration", "Integration")]
    public async Task NbxAuthoritative_WalletTracksMigratesAndFlipsToNbx_BalanceAndPaymentDetected()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        // Migration applies: GenerateWallet's ElectrumHttpHandler branch calls
        // ElectrumWalletTracker.SetMetadataAsync synchronously in the request, and every
        // ElectrumWalletTracker method starts with EnsureMigratedAsync (Electrum/
        // ElectrumWalletTracker.cs) — so by the time RegisterDerivationScheme's HTTP round
        // trip has returned, the "electrum" schema should already exist, independent of
        // whether Electrum itself is reachable (contrast with ElectrumListener.RunAsync,
        // which only calls tracker.InitializeAsync() after the Electrum client connects —
        // that path never fires here since Electrum is unreachable on regtest).
        await using (var conn = new NpgsqlConnection(tester.PayTester.Postgres))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'electrum' AND table_name = 'tracked_wallets'";
            var count = (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal(1, count);
        }

        // Backend flip: a brand-new wallet defaults to WalletBackend.Electrum
        // (BackendCoordinator.GetActiveBackend) until BackendCoordinator.EvaluateWalletAsync
        // has agreed "Nbx" for HysteresisGate.RequiredToNbx (=4) consecutive evaluations.
        // The mirror-Track background task (ElectrumHttpHandler.cs) triggers one evaluation
        // immediately, and the coordinator's own 30s poll loop (BackendCoordinator.PollInterval)
        // supplies the rest, so this can take a few poll cycles (~2 minutes) even though NBX
        // is synced and tracking the wallet the whole time.
        var coordinator = tester.PayTester.GetService<BackendCoordinator>();
        await TestUtils.EventuallyAsync(() =>
        {
            Assert.Equal(WalletBackend.Nbx, coordinator.GetActiveBackend(walletId));
            return Task.CompletedTask;
        }, delay: 200_000);

        // Once NBX-active, wallet reads/writes route to real NBX (ElectrumHttpHandler ->
        // NbxRequestClassifier -> BackendCoordinator.GetActiveBackend == Nbx). Receive a mined
        // payment and confirm BTCPayWallet (going through the Electrum shim) reports the UTXO —
        // i.e. balance/receive-address/payment-detection all work end-to-end via NBX.
        var coin = await user.ReceiveUTXO(Money.Coins(0.5m));
        Assert.NotNull(coin);
        Assert.Equal(Money.Coins(0.5m), coin.Amount);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Integration", "Integration")]
    public async Task CoexistencePanel_HonorsAllowNonMainnetEscapeHatch_AndRendersOk()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();

        var user = tester.NewAccount();
        user.GrantAccess(isAdmin: true);

        var controller = user.GetController<UIElectrumController>();

        // TestAccount.GetController<T>() builds the controller directly (not through the real
        // MVC action-invoker pipeline), so filter overrides like OnActionExecuting don't run
        // automatically — invoke it explicitly to check the exact mainnet-gate behavior found
        // during this task (see UIElectrumController.OnActionExecuting comment + the fix
        // committed alongside these tests): with the ALLOWNONMAINNET escape hatch set (as it is
        // for this whole test class, via ElectrumCoexistenceTestsBase), the gate must NOT
        // short-circuit to NotFound even though NetworkType is Regtest, because
        // ElectrumPlugin.Execute did activate (BackendCoordinator etc. are registered) and the
        // action can serve a real 200.
        // TestAccount.GetController<T>() (BTCPayServerTester.GetController) builds
        // ControllerContext with only HttpContext set — RouteData and ActionDescriptor are
        // left null. ActionExecutingContext's underlying ActionContext constructor requires
        // both to be non-null, so without this they throw ArgumentNullException(routeData)
        // before OnActionExecuting ever runs.
        controller.ControllerContext.RouteData = new RouteData();
        controller.ControllerContext.ActionDescriptor = new ControllerActionDescriptor();

        var executingContext = new ActionExecutingContext(
            controller.ControllerContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller);
        controller.OnActionExecuting(executingContext);
        Assert.Null(executingContext.Result);

        var result = await controller.Settings();
        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.IsType<ElectrumSettings>(viewResult.Model);
        Assert.NotNull(controller.ViewBag.Coexistence);
    }

    // ── Electrum-serving legs: need a regtest Electrum server (Fulcrum) — follow-up ──
    // ElectrumClient.TrustedServers are all public mainnet servers, unreachable from the
    // regtest ServerTester stack, so Electrum is permanently down here. These scenarios all
    // require Electrum to actually be reachable and cannot be faked without weakening the
    // assertion into meaninglessness (e.g. "no duplicate event" is trivially true if Electrum
    // never publishes anything at all).

    [Fact(Timeout = 180_000)]
    [Trait("Integration", "Integration")]
    public async Task ElectrumServing_WalletBalanceAndPayment_WorksEndToEnd()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        // Point the singleton Electrum client at the local regtest Fulcrum and confirm a real
        // Electrum handshake — server.version must come back as Fulcrum, proving we reached the
        // regtest Electrum server (not a stale mainnet TrustedServer and not just an open socket).
        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);
        var client = tester.PayTester.GetService<ElectrumClient>();
        var (software, _) = await client.ServerVersionAsync("BTCPayServer-Electrum-Test", "1.4", ct);
        Assert.Contains("Fulcrum", software, StringComparison.OrdinalIgnoreCase);

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        // Registering the scheme fires the handler's background TrackWalletAsync: it derives the
        // gap-limit addresses, subscribes their scripthashes on Fulcrum, and syncs current state.
        // Derive the deposit-index-0 address directly from the scheme (not via the tracker) so we
        // never win the derive race that would make TrackWalletAsync skip its subscribe step.
        var network = tester.PayTester.GetService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>("BTC");
        var depositAddress = user.DerivationScheme
            .GetLineFor(DerivationFeature.Deposit).Derive(0)
            .ScriptPubKey.GetDestinationAddress(network.NBitcoinNetwork);

        // Pay + confirm on the regtest bitcoind Fulcrum indexes.
        await tester.ExplorerNode.SendToAddressAsync(depositAddress, Money.Coins(0.5m));
        tester.ExplorerNode.Generate(1);

        // The Electrum engine — reading only its own DB, populated by Fulcrum scripthash
        // notifications / initial sync — must report the confirmed balance and the UTXO. This is
        // the first proof the Electrum-serving half works end-to-end against a real regtest server.
        var tracker = tester.PayTester.GetService<ElectrumWalletTracker>();
        await TestUtils.EventuallyAsync(async () =>
        {
            var balance = await tracker.GetBalanceAsync(walletId, ct);
            Assert.Equal(Money.Coins(0.5m), balance.Total);
        }, delay: 120_000);

        var utxos = await tracker.GetUTXOChangesAsync(walletId, ct);
        Assert.Contains(utxos.Confirmed.UTXOs, u => u.Value is Money m && m == Money.Coins(0.5m));
    }

    [Fact(Timeout = 240_000)]
    [Trait("Integration", "Integration")]
    public async Task ReserveFlipNoReuse_AcrossBackendSwitch_ExercisesFastForwardToIndexAsync()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        var coordinator = tester.PayTester.GetService<BackendCoordinator>();
        var tracker = tester.PayTester.GetService<ElectrumWalletTracker>();
        var realNbx = tester.PayTester.GetService<RealNbxGateway>();
        var network = tester.PayTester.GetService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>("BTC");
        var strategy = new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(walletId);

        // Reserve several deposit addresses via Electrum to advance its reserved high-water
        // (recorded in the shared ReservedIndexLedger).
        var electrumHigh = -1;
        for (var i = 0; i < 5; i++)
        {
            var info = await tracker.GetNextUnusedAddressAsync(walletId, false, true, ct);
            electrumHigh = (int)info.KeyPath.Indexes[^1];
        }

        // Flip to NBX. On the transition the IndexFastForwarder burns NBX's deposit addresses up
        // to the reserved high-water, so NBX cannot re-hand-out an index Electrum already reserved.
        await ElectrumTestInfra.ForceBackendAsync(coordinator, walletId, WalletBackend.Nbx, ct);

        // The next address NBX reserves must be strictly beyond Electrum's high-water — no reuse.
        var nbx = realNbx.GetClient("BTC");
        var nbxUnused = await nbx.GetUnusedAsync(strategy, DerivationFeature.Deposit, 0, true, ct);
        var nbxIndex = (int)nbxUnused.KeyPath.Indexes[^1];
        Assert.True(nbxIndex > electrumHigh,
            $"NBX reused index {nbxIndex}, expected strictly greater than Electrum high-water {electrumHigh}");
    }

    [Fact(Timeout = 240_000)]
    [Trait("Integration", "Integration")]
    public async Task NbxDown_FailsBackToElectrum()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        var coordinator = tester.PayTester.GetService<BackendCoordinator>();

        // First become NBX-authoritative: NBX is synced and, once the mirror-Track lands,
        // tracking the wallet, so the hysteresis gate promotes it to Nbx.
        await ElectrumTestInfra.ForceBackendAsync(coordinator, walletId, WalletBackend.Nbx, ct);

        try
        {
            // Take NBX down. Its status/tracking calls now fail (connection refused), so each
            // evaluation decides Electrum; after RequiredToElectrum consecutive votes + cooldown
            // the wallet fails back to the still-connected Electrum backend.
            ElectrumTestInfra.StopNbx();
            await ElectrumTestInfra.ForceBackendAsync(coordinator, walletId, WalletBackend.Electrum, ct);
            Assert.Equal(WalletBackend.Electrum, coordinator.GetActiveBackend(walletId));
        }
        finally
        {
            ElectrumTestInfra.StartNbx();
        }
    }

    [Fact(Timeout = 240_000)]
    [Trait("Integration", "Integration")]
    public async Task NoDuplicateNewOnChainTransactionEvent_ForNbxActiveWallet()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        var coordinator = tester.PayTester.GetService<BackendCoordinator>();
        await ElectrumTestInfra.ForceBackendAsync(coordinator, walletId, WalletBackend.Nbx, ct);

        // Both backends track this wallet (NBX active + Electrum mirror on Fulcrum), so both
        // listeners observe the tx. Assert on the EventAggregator — which, unlike the payments
        // table, is NOT primary-key deduplicated (both listeners would otherwise mint the same
        // "{txid}-{vout}" payment id and a duplicate row would be silently dropped, hiding a gate
        // leak). Electrum publishes a *confirmed* tx with a null BlockId (its per-tx stream has no
        // block hash), whereas the core NBXplorerListener sets a real BlockId once confirmed, so a
        // confirmed-tx event with BlockId == null could only have come from the Electrum listener
        // — i.e. the EventGate leaked for an NBX-active wallet.
        var eventAggregator = tester.PayTester.GetService<EventAggregator>();
        var total = 0;
        var electrumShaped = 0;
        using var sub = eventAggregator.Subscribe<NewOnChainTransactionEvent>(evt =>
        {
            var nte = evt.NewTransactionEvent;
            if (nte?.DerivationStrategy?.ToString() != walletId)
                return;
            Interlocked.Increment(ref total);
            if (nte.BlockId == null && nte.TransactionData?.Confirmations > 0)
                Interlocked.Increment(ref electrumShaped);
        });

        var network = tester.PayTester.GetService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>("BTC");
        var depositAddress = user.DerivationScheme
            .GetLineFor(DerivationFeature.Deposit).Derive(1)
            .ScriptPubKey.GetDestinationAddress(network.NBitcoinNetwork);

        await tester.ExplorerNode.SendToAddressAsync(depositAddress, Money.Coins(0.3m));
        tester.ExplorerNode.Generate(1);

        // Wait until the payment is observed at all (proves the scenario actually ran — else the
        // assertion below is vacuous), then hold long enough that a leaked Electrum publish for
        // the confirmed tx would have landed.
        await TestUtils.EventuallyAsync(() =>
        {
            Assert.True(total >= 1, "no NewOnChainTransactionEvent observed for the wallet");
            return Task.CompletedTask;
        }, delay: 120_000);
        await Task.Delay(15_000, ct);

        // NBX published; the gate kept Electrum silent. If the gate were removed, Electrum would
        // publish a confirmed-tx-with-null-BlockId event here and this would be >= 1.
        Assert.Equal(0, electrumShaped);
    }

    [Fact(Timeout = 180_000)]
    [Trait("Integration", "Integration")]
    public async Task Dashboard_AvailableWhenElectrumUpAndNbxUnsynced()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        // Reconciliation rule (pure): either backend alone keeps BTC available.
        Assert.True(ElectrumStatusMonitor.EffectiveSynced(nbxSynced: false, electrumConnected: true));
        Assert.False(ElectrumStatusMonitor.EffectiveSynced(nbxSynced: false, electrumConnected: false));

        var monitor = tester.PayTester.GetService<ElectrumStatusMonitor>();
        var dashboard = tester.PayTester.GetService<NBXplorerDashboard>();
        try
        {
            // NBX unreachable, Electrum (Fulcrum) up: the monitor must keep BTC reported Ready so
            // the store dashboard stays usable, instead of dropping to NotConnected when NBX dies.
            ElectrumTestInfra.StopNbx();
            await TestUtils.EventuallyAsync(() =>
            {
                Assert.Equal(NBXplorerState.Ready, monitor.State);
                Assert.True(dashboard.IsFullySynched());
                return Task.CompletedTask;
            }, delay: 90_000);
        }
        finally
        {
            ElectrumTestInfra.StartNbx();
        }
    }

    [Fact(Timeout = 240_000)]
    [Trait("Integration", "Integration")]
    public async Task Hysteresis_OnePollNbxBlip_DoesNotFlipWallet()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        var coordinator = tester.PayTester.GetService<BackendCoordinator>();
        await ElectrumTestInfra.ForceBackendAsync(coordinator, walletId, WalletBackend.Nbx, ct);

        // A single-poll NBX blip: exactly one evaluation sees NBX unreachable. RequiredToElectrum
        // is 3, so one Electrum vote is far short of a flip — the wallet must stay on Nbx.
        try
        {
            ElectrumTestInfra.StopNbx();
            await coordinator.EvaluateWalletAsync(walletId, ct); // the blip
        }
        finally
        {
            ElectrumTestInfra.StartNbx();
        }
        Assert.Equal(WalletBackend.Nbx, coordinator.GetActiveBackend(walletId));

        // Once NBX recovers, subsequent evaluations agree Nbx again; it never left Nbx.
        await ElectrumTestInfra.ForceBackendAsync(coordinator, walletId, WalletBackend.Nbx, ct);
        Assert.Equal(WalletBackend.Nbx, coordinator.GetActiveBackend(walletId));
    }

    [Fact(Timeout = 180_000)]
    [Trait("Integration", "Integration")]
    public async Task NbxDown_GlobalReadFallsBackToElectrum_AndHealthFlips()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        // The store-facing ExplorerClient shim: its REST transport is ElectrumHttpHandler, so
        // these calls exercise the real routing (proxy-to-NBX vs serve-from-Electrum).
        var network = tester.PayTester.GetService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>("BTC");
        var shim = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(network);
        var health = tester.PayTester.GetService<NbxHealth>();

        // Sanity: with NBX up, a Global read resolves.
        Assert.NotNull(await shim.GetStatusAsync(ct));

        try
        {
            ElectrumTestInfra.StopNbx();

            // A GlobalRead (status) through the shim must NOT throw with NBX down: the router tries
            // NBX, catches the transport failure, marks NBX unreachable, and serves from the
            // Electrum engine. Broadcast takes the identical routing path, so this also covers the
            // send-during-outage fallback.
            await TestUtils.EventuallyAsync(async () =>
            {
                var status = await shim.GetStatusAsync(ct);
                Assert.NotNull(status);
                Assert.False(health.Reachable);
            }, delay: 30_000);
        }
        finally
        {
            ElectrumTestInfra.StartNbx();
        }
    }

    [Fact(Timeout = 240_000)]
    [Trait("Integration", "Integration")]
    public async Task NbxRecovery_FiresRescan_AndNbxRediscoversOutageActivity()
    {
        using var tester = CreateElectrumServerTester();
        await tester.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        await ElectrumTestInfra.PointElectrumAtFulcrumAsync(tester, ct);

        var user = tester.NewAccount();
        user.GrantAccess();
        user.RegisterDerivationScheme("BTC");
        var walletId = user.DerivationScheme.ToString();

        var health = tester.PayTester.GetService<NbxHealth>();
        var recovered = false;
        health.OnRecovered += () => recovered = true;

        var coordinator = tester.PayTester.GetService<BackendCoordinator>();
        var realNbx = tester.PayTester.GetService<RealNbxGateway>();
        var network = tester.PayTester.GetService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>("BTC");
        var strategy = new DerivationStrategyFactory(network.NBitcoinNetwork).Parse(walletId);
        var depositAddress = user.DerivationScheme
            .GetLineFor(DerivationFeature.Deposit).Derive(2)
            .ScriptPubKey.GetDestinationAddress(network.NBitcoinNetwork);

        var nbxStopped = false;
        try
        {
            // NBX goes down; one evaluation flips the shared health flag to unreachable.
            ElectrumTestInfra.StopNbx();
            nbxStopped = true;
            await coordinator.EvaluateWalletAsync(walletId, ct);
            Assert.False(health.Reachable);

            // Funds arrive while NBX is down (only the Electrum engine could see them live).
            await tester.ExplorerNode.SendToAddressAsync(depositAddress, Money.Coins(0.4m));
            tester.ExplorerNode.Generate(1);

            // NBX comes back (StartNbx blocks until it reports synced); one evaluation records it
            // reachable again, which fires OnRecovered -> the coordinator rescans tracked wallets.
            ElectrumTestInfra.StartNbx();
            nbxStopped = false;
            await coordinator.EvaluateWalletAsync(walletId, ct);
            Assert.True(recovered, "NBX recovery did not fire the rescan trigger");

            // Recovery data-sync: after NBX's own chain re-index + the triggered rescan, NBX must
            // rediscover the payment that arrived during the outage.
            var nbx = realNbx.GetClient("BTC");
            await TestUtils.EventuallyAsync(async () =>
            {
                var utxos = await nbx.GetUTXOsAsync(strategy, ct);
                Assert.Contains(utxos.Confirmed.UTXOs, u => u.Value is Money m && m == Money.Coins(0.4m));
            }, delay: 150_000);
        }
        finally
        {
            if (nbxStopped)
                ElectrumTestInfra.StartNbx();
        }
    }
}
