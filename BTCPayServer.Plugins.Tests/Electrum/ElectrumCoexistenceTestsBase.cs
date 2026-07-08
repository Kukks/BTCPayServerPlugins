using System;
using System.IO;
using System.Runtime.CompilerServices;
using BTCPayServer.Plugins.Electrum;
using Xunit;

namespace BTCPayServer.Tests.Electrum;

// Base for the Electrum <-> NBX coexistence integration tests (P4 Task 4). These are the
// first tests in the ~23-commit coexistence branch that actually boot a ServerTester host
// with the Electrum plugin loaded — everything before this task was unit/compile-level only.
//
// How the plugin gets loaded into ServerTester (the "hard part" this task had to solve):
//
// BTCPayServerTester (submodules/btcpayserver/BTCPayServer.Tests/BTCPayServerTester.cs)
// already has first-class support for testing plugins
// (see https://github.com/btcpayserver/btcpayserver/pull/7008,
// property `LoadPluginsInDefaultAssemblyContext`, default true): it sets the in-memory
// config key TEST_RUNNER_ENABLED=true, which PluginManager.AddPlugins
// (submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs) reads via
// config.GetOrDefault&lt;bool&gt;("TEST_RUNNER_ENABLED") to force McMaster's PluginLoader to
// resolve the plugin assembly from the *already-loaded* default AssemblyLoadContext, instead
// of loading a second copy of it. Since this test project ProjectReferences the Electrum
// plugin project directly, the Electrum assembly is already loaded there — this avoids the
// duplicate-type-identity problems that would otherwise break casts/DI between "the test"
// and "the plugin".
//
// The actual load hook is PluginManager's DEBUG_PLUGINS config key (only compiled under
// `#if DEBUG`, which `dotnet test`'s default Debug configuration satisfies):
//   "&lt;PLUGIN_IDENTIFIER&gt;::&lt;AbsoluteDllPath&gt;"
// DefaultConfiguration.EnvironmentVariablePrefix is "BTCPAY_", and
// Microsoft.Extensions.Configuration keys are case-insensitive, so setting the environment
// variable BTCPAY_DEBUG_PLUGINS is read by PluginManager as config["DEBUG_PLUGINS"] — exactly
// the same env-var-prefix mechanism ElectrumPlugin's own BTCPAY_ELECTRUM_ALLOWNONMAINNET
// escape hatch uses (ElectrumPlugin.cs: config.GetValue&lt;bool&gt;("ELECTRUM_ALLOWNONMAINNET", false)).
//
// Both env vars are set here, before the ServerTester's host is built (DefaultConfiguration's
// ConfigurationBuilder.Build() reads them at that point), so no submodule changes are needed.
public abstract class ElectrumCoexistenceTestsBase : UnitTestBase
{
    protected ElectrumCoexistenceTestsBase(ITestOutputHelper helper) : base(helper)
    {
    }

    // Same identifier resolution BaseBTCPayServerPlugin uses by default: the plugin
    // assembly's own name (BTCPayServer.Plugins.Electrum.csproj -> AssemblyName).
    public const string ElectrumPluginIdentifier = "BTCPayServer.Plugins.Electrum";

    protected ServerTester CreateElectrumServerTester([CallerMemberName] string? scope = null)
    {
        var pluginDll = typeof(ElectrumPlugin).Assembly.Location;
        Environment.SetEnvironmentVariable("BTCPAY_ELECTRUM_ALLOWNONMAINNET", "true");

        // BTCPayServerTester builds config as: env vars, THEN appsettings.dev.json from
        // TestUtils.TestDirectory (SetBasePath + AddJsonFile), THEN in-memory TEST_RUNNER_ENABLED.
        // So that appsettings.dev.json's DEBUG_PLUGINS — the whole repo's plugin set, written into the
        // test output by the ConfigBuilder project — overrides BTCPAY_DEBUG_PLUGINS. Several of those
        // plugins (Blink, MCP, NIP05, Stripe) can't resolve their dependency assemblies in this test's
        // default load context and crash host startup (ConfigException), failing every Electrum test
        // before it runs. Overwrite that exact file so ONLY the Electrum plugin loads.
        var devSettings = Path.Combine(TestUtils.TestDirectory, "appsettings.dev.json");
        File.WriteAllText(devSettings,
            $"{{\"DEBUG_PLUGINS\":\"{ElectrumPluginIdentifier}::{pluginDll.Replace("\\", "/")}\"}}");

        return CreateServerTester(scope);
    }
}
