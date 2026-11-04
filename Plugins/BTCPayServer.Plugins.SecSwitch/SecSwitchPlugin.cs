using System;
using System.Net.Http;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SecSwitch;

public class SecSwitchPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("server-nav", "SecSwitch/Nav");
        services.AddUIExtension("layout-banner", "SecSwitch/AlertBanner");

        // A named client rather than a typed one: the periodic task is a singleton, so a typed client
        // would pin one handler (and its DNS result) for the life of the process.
        services.AddHttpClient(SecSwitchPeriodicTask.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<LedgerStore>();
        services.AddSingleton<LockdownGate>();
        services.AddTransient<IStartupFilter, LockdownStartupFilter>();
        services.AddSingleton<IActionSink, BtcPayActionSink>();
        services.AddSingleton<ActionExecutor>();
        services.AddSingleton<SecSwitchMonitor>();
        services.AddSingleton<INotificationHandler, SecSwitchNotification.Handler>();
        services.AddScheduledTask<SecSwitchPeriodicTask>(TimeSpan.FromHours(1));

        base.Execute(services);
    }
}
