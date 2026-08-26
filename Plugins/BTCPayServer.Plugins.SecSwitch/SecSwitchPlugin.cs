using System;
using System.Net.Http;
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
        services.AddUIExtension("layout-banner", "SecSwitch/AlertBanner");

        // Deviation from the Task 13 brief (ruling applied 2026-08-26): the brief registers
        // services.AddHttpClient<AdvisoryFetcher>() (a typed client). AddScheduledTask<T> registers T
        // (SecSwitchPeriodicTask) as a singleton (core, Extensions.cs: services.TryAddSingleton<T>()),
        // so constructor-injecting AdvisoryFetcher into it would capture one HttpClient - and its
        // handler/connection pool - for the life of the process, never rotating and going DNS-stale on
        // a long-running server. A NAMED client is registered instead; SecSwitchPeriodicTask resolves
        // IHttpClientFactory and constructs a fresh AdvisoryFetcher, from a fresh client, on every poll.
        //
        // Task 13 review, Finding M1 (Minor): AdvisoryFetcher's own class doc comment asks callers to
        // set AllowAutoRedirect = false on the handler backing its HttpClient as defence in depth -
        // GetBytesAsync's post-response containment re-check (IsUnderBase against
        // response.RequestMessage.RequestUri) is the real guarantee and does not depend on this, but
        // this plugin is now that caller, so it honours the ask rather than relying solely on the
        // downstream check.
        services.AddHttpClient(SecSwitchPeriodicTask.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<LedgerStore>();
        services.AddSingleton<IActionSink, BtcPayActionSink>();
        services.AddSingleton<ActionExecutor>();
        services.AddSingleton<SecSwitchMonitor>();
        services.AddSingleton<INotificationHandler, SecSwitchNotification.Handler>();
        services.AddScheduledTask<SecSwitchPeriodicTask>(TimeSpan.FromHours(1));

        base.Execute(services);
    }
}
