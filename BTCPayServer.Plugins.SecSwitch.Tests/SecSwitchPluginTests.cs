using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class SecSwitchPluginTests
{
    static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        new SecSwitchPlugin().Execute(services);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("layout-banner", "SecSwitch/AlertBanner")]
    [InlineData("server-nav", "SecSwitch/Nav")]
    public void Execute_registers_the_ui_extensions(string location, string partial)
    {
        using var provider = BuildProvider();

        Assert.Contains(provider.GetServices<IUIExtension>(), e => e.Location == location && e.Partial == partial);
    }

    [Fact]
    public void Execute_registers_the_periodic_task_hourly()
    {
        using var provider = BuildProvider();

        var task = Assert.Single(provider.GetServices<ScheduledTask>(), s => s.PeriodicTaskType == typeof(SecSwitchPeriodicTask));
        Assert.Equal(TimeSpan.FromHours(1), task.Every);
    }

    [Fact]
    public void Execute_registers_the_lockdown_startup_filter()
    {
        using var provider = BuildProvider();

        Assert.Contains(provider.GetServices<IStartupFilter>(), f => f is LockdownStartupFilter);
    }

    [Fact]
    public void Execute_registers_the_named_feed_client()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>().CreateClient(SecSwitchPeriodicTask.HttpClientName));
    }
}
