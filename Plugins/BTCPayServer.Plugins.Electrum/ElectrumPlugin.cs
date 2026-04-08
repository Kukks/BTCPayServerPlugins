using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Electrum.Data;
using BTCPayServer.Plugins.Electrum.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Fees;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Electrum;

public class ElectrumPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        // ──────────────────────────────────────────────
        // 1. Remove NBXplorer services
        // ──────────────────────────────────────────────

        // ExplorerClientProvider (singleton + interface registration)
        RemoveByImplementation<ExplorerClientProvider>(services);
        RemoveByServiceType<Common.IExplorerClientProvider>(services);

        // NBXplorerConnectionFactory (singleton + hosted service)
        RemoveByImplementation<NBXplorerConnectionFactory>(services);
        RemoveHostedService<NBXplorerConnectionFactory>(services);

        // NBXplorerListener (hosted service)
        RemoveHostedService<NBXplorerListener>(services);

        // NBXplorerWaiters (hosted service)
        RemoveHostedService<NBXplorerWaiters>(services);

        // NBXplorerDashboard
        RemoveByImplementation<NBXplorerDashboard>(services);

        // ISyncSummaryProvider
        RemoveByServiceType<ISyncSummaryProvider>(services);

        // FeeProviderFactory (singleton + interface + scheduled task)
        RemoveByImplementation<FeeProviderFactory>(services);
        RemoveByServiceType<IFeeProviderFactory>(services);
        RemoveScheduledTask<FeeProviderFactory>(services);

        // ──────────────────────────────────────────────
        // 2. Register Electrum engine
        // ──────────────────────────────────────────────

        services.AddSingleton<ElectrumClient>();
        services.AddSingleton<ElectrumWalletTracker>();

        // DB context
        services.AddSingleton<ElectrumDbContextFactory>();
        services.AddDbContext<ElectrumDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<ElectrumDbContextFactory>();
            factory.ConfigureBuilder(o);
        });

        // HTTP handler for shimming ExplorerClient calls
        services.AddSingleton<ElectrumHttpHandler>();

        // ──────────────────────────────────────────────
        // 4. Register shadow services
        // ──────────────────────────────────────────────

        // NBXplorerDashboard - reuse same type, we populate it from ElectrumStatusMonitor
        services.AddSingleton<NBXplorerDashboard>();

        // ExplorerClientProvider replacement
        services.AddSingleton<ElectrumExplorerClientProvider>();
        services.AddSingleton<ExplorerClientProvider>(sp => sp.GetRequiredService<ElectrumExplorerClientProvider>());
        services.AddSingleton<Common.IExplorerClientProvider>(sp => sp.GetRequiredService<ElectrumExplorerClientProvider>());

        // NBXplorerConnectionFactory replacement (Available = false)
        services.AddSingleton<ElectrumConnectionFactory>();
        services.AddSingleton<NBXplorerConnectionFactory>(sp => sp.GetRequiredService<ElectrumConnectionFactory>());

        // Fee estimation
        services.AddSingleton<ElectrumFeeProvider>();
        services.AddSingleton<ElectrumFeeProviderFactory>();
        services.AddSingleton<IFeeProviderFactory>(sp => sp.GetRequiredService<ElectrumFeeProviderFactory>());

        // Status monitoring (replaces NBXplorerWaiters)
        services.AddSingleton<ElectrumStatusMonitor>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<ElectrumStatusMonitor>());

        // Payment listener (replaces NBXplorerListener)
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, ElectrumListener>();

        // Sync summary
        services.AddSingleton<ElectrumSyncSummaryProvider>();
        services.AddSingleton<ISyncSummaryProvider>(sp => sp.GetRequiredService<ElectrumSyncSummaryProvider>());

        // ──────────────────────────────────────────────
        // 5. Admin UI
        // ──────────────────────────────────────────────

        services.AddUIExtension("server-nav", "Electrum/NavExtension");
    }

    private static void RemoveByImplementation<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ImplementationType == typeof(T) ||
                                              (d.ImplementationFactory?.Method.ReturnType == typeof(T)) ||
                                              d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }

    private static void RemoveByServiceType<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);
    }

    private static void RemoveScheduledTask<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(ScheduledTask) &&
            d.ImplementationFactory != null).ToList();
        foreach (var d in descriptors)
        {
            // ScheduledTask factories capture the type in their closure.
            // Instantiate to check PeriodicTaskType.
            try
            {
                var instance = (ScheduledTask)d.ImplementationFactory(null!);
                if (instance.PeriodicTaskType == typeof(T))
                    services.Remove(d);
            }
            catch
            {
                // Factory might need a real provider — skip
            }
        }
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
            (d.ImplementationType == typeof(T) ||
             d.ImplementationFactory != null)).ToList();

        // Be selective - only remove if the implementation type matches
        foreach (var d in descriptors)
        {
            if (d.ImplementationType == typeof(T))
            {
                services.Remove(d);
            }
            else if (d.ImplementationFactory != null)
            {
                // Check if the factory resolves to our type
                // For factories like: sp => sp.GetRequiredService<T>()
                // The return type or generic args might indicate the type
                var factoryMethod = d.ImplementationFactory.Method;
                if (factoryMethod.ReturnType == typeof(T) ||
                    factoryMethod.ToString()?.Contains(typeof(T).Name) == true)
                {
                    services.Remove(d);
                }
            }
        }
    }
}
