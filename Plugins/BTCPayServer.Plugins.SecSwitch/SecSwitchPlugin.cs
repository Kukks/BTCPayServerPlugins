using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SecSwitch;

public class SecSwitchPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.2" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("server-nav", "SecSwitch/Nav");

        services.AddHttpClient<AdvisoryFetcher>();
        services.AddSingleton<LedgerStore>();
        services.AddSingleton<IActionSink, BtcPayActionSink>();
        services.AddSingleton<ActionExecutor>();
        services.AddSingleton<SecSwitchMonitor>();
        services.AddSingleton<INotificationHandler, SecSwitchNotification.Handler>();

        // Deviation from the Task 12 brief (ruling applied 2026-08-26): the brief's Step 5 also
        // registers services.AddScheduledTask<SecSwitchPeriodicTask>(TimeSpan.FromHours(1)) and
        // services.AddUIExtension("layout-banner", ...), but SecSwitchPeriodicTask does not exist
        // until Task 13, and no layout-banner partial exists yet either - both lines would fail to
        // compile today. Everything else is registered here; Task 13 adds those two lines back as
        // its first step, keeping this task independently buildable.

        base.Execute(services);
    }
}
