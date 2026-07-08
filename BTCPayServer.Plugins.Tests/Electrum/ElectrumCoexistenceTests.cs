using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Electrum;
using BTCPayServer.Plugins.Electrum.Controllers;
using BTCPayServer.Plugins.Electrum.Services;
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

    [Fact]
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

    [Fact]
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

    [Fact(Skip = "needs regtest Electrum server (Fulcrum) — follow-up")]
    public Task ElectrumServing_WalletBalanceAndPayment_WorksEndToEnd() => Task.CompletedTask;

    [Fact(Skip = "needs regtest Electrum server (Fulcrum) — follow-up")]
    public Task ReserveFlipNoReuse_AcrossBackendSwitch_ExercisesFastForwardToIndexAsync() => Task.CompletedTask;

    [Fact(Skip = "needs regtest Electrum server (Fulcrum) — follow-up")]
    public Task NbxDown_FailsBackToElectrum() => Task.CompletedTask;

    [Fact(Skip = "needs regtest Electrum server (Fulcrum) — follow-up")]
    public Task NoDuplicateNewOnChainTransactionEvent_ForNbxActiveWallet() => Task.CompletedTask;

    [Fact(Skip = "needs regtest Electrum server (Fulcrum) — follow-up")]
    public Task Dashboard_AvailableWhenElectrumUpAndNbxUnsynced() => Task.CompletedTask;

    [Fact(Skip = "needs regtest Electrum server (Fulcrum) — follow-up")]
    public Task Hysteresis_OnePollNbxBlip_DoesNotFlipWallet() => Task.CompletedTask;
}
