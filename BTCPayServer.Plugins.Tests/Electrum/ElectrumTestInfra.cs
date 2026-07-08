using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Electrum;
using BTCPayServer.Plugins.Electrum.Services;
using BTCPayServer.Services;
using Xunit;

namespace BTCPayServer.Tests.Electrum;

// Shared infrastructure for the Electrum-serving integration legs (the ones that need a real
// regtest Electrum server). The merged docker stack for these tests is brought up with:
//   docker compose -f submodules/btcpayserver/BTCPayServer.Tests/docker-compose.yml \
//                  -f BTCPayServer.Plugins.Tests/docker-compose.electrum.yml \
//                  -p btcpayservertests up -d dev fulcrum
// which adds a Fulcrum (Electrum protocol server) on 127.0.0.1:50001 wired to the tester bitcoind.
internal static class ElectrumTestInfra
{
    // Fulcrum from docker-compose.electrum.yml (regtest, no TLS).
    public const string FulcrumServer = "127.0.0.1:50001";

    // The nbxplorer container name for the -p btcpayservertests project. Overridable via env
    // var for a differently-named stack; defaults to the name our supplement compose produces.
    public static string NbxContainer =>
        Environment.GetEnvironmentVariable("ELECTRUM_TEST_NBX_CONTAINER") ?? "btcpayservertests-nbxplorer-1";

    /// <summary>
    /// Persist ElectrumSettings pointing at the local regtest Fulcrum and force a clean
    /// (re)connect so the singleton client leaves any stale/mainnet server. Relies on
    /// ElectrumClient reloading settings on reconnect. Returns once the client has completed a
    /// real Electrum handshake with Fulcrum (ConnectedServer + a server.version reply).
    /// </summary>
    public static async Task PointElectrumAtFulcrumAsync(ServerTester tester, CancellationToken ct)
    {
        var settingsRepo = tester.PayTester.GetService<SettingsRepository>();
        await settingsRepo.UpdateSetting(new ElectrumSettings
        {
            Server = FulcrumServer,
            UseTls = false,
            CryptoCode = "BTC"
        });

        var client = tester.PayTester.GetService<ElectrumClient>();
        await client.DisconnectAsync();

        // Drive the connect directly instead of waiting for the monitor's (up to 60s) loop.
        await TestUtils.EventuallyAsync(async () =>
        {
            if (!client.IsConnected)
                await client.ConnectAsync(ct);
            Assert.Equal(FulcrumServer, client.ConnectedServer);
        }, delay: 30_000);
    }

    public static void StopNbx() => RunDocker($"stop {NbxContainer}");
    public static void StartNbx() => RunDocker($"start {NbxContainer}");

    private static void RunDocker(string args)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null)
            throw new InvalidOperationException("Failed to start docker process");
        p.WaitForExit(60_000);
    }

    /// <summary>
    /// Wait until the coordinator reports the given active backend for a wallet. The flip is
    /// hysteresis-gated over 30s polls, so callers should allow a couple of minutes.
    /// </summary>
    public static Task WaitBackendAsync(BackendCoordinator coordinator, string walletId,
        WalletBackend expected, int timeoutMs) =>
        TestUtils.EventuallyAsync(() =>
        {
            Assert.Equal(expected, coordinator.GetActiveBackend(walletId));
            return Task.CompletedTask;
        }, delay: timeoutMs);

    /// <summary>
    /// Drive backend evaluations directly instead of waiting on the coordinator's 30s poll loop.
    /// Each EvaluateWalletAsync call is exactly one poll's worth of work, so this accumulates
    /// hysteresis votes (and cooldown polls) deterministically and quickly. A per-iteration delay
    /// gives asynchronous prerequisites (the mirror-Track landing in NBX, an NBX (re)sync, a
    /// scripthash subscribe) time to take effect between votes. Asserts the flip happened.
    /// </summary>
    public static async Task ForceBackendAsync(BackendCoordinator coordinator, string walletId,
        WalletBackend expected, CancellationToken ct, int maxPolls = 20, int delayMs = 1000)
    {
        for (var i = 0; i < maxPolls; i++)
        {
            if (coordinator.GetActiveBackend(walletId) == expected)
                return;
            await coordinator.EvaluateWalletAsync(walletId, ct);
            if (coordinator.GetActiveBackend(walletId) == expected)
                return;
            await Task.Delay(delayMs, ct);
        }
        Assert.Equal(expected, coordinator.GetActiveBackend(walletId));
    }
}
