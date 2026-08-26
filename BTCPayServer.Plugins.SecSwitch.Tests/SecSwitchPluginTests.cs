using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.SecSwitch.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// <summary>
/// Task 13 Step 1: SecSwitchPlugin.Execute registers the scheduled task and the layout-banner
/// extension - the two lines Task 12 deliberately left commented out until SecSwitchPeriodicTask and
/// its view existed (see task-12-report.md and the git history of SecSwitchPlugin.cs). Resolving
/// IEnumerable&lt;IUIExtension&gt; and IEnumerable&lt;ScheduledTask&gt; is safe against a bare
/// ServiceCollection with nothing else registered: both are populated at REGISTRATION time
/// (AddUIExtension stores an already-constructed UIExtension instance; AddScheduledTask's
/// ScheduledTask factory only wraps `typeof(T)` and `every`, and never itself constructs a T) - unlike
/// resolving SecSwitchPeriodicTask itself, which needs the rest of BTCPayServer's DI graph
/// (ISettingsRepository, NotificationSender, BTCPayServerEnvironment, ...) this test suite does not
/// attempt to assemble.
/// </summary>
public class SecSwitchPluginTests
{
    static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        new SecSwitchPlugin().Execute(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Execute_registers_the_layout_banner_alert()
    {
        using var provider = BuildProvider();
        var extensions = provider.GetServices<IUIExtension>().ToList();

        Assert.Contains(extensions, e => e.Location == "layout-banner" && e.Partial == "SecSwitch/AlertBanner");
    }

    [Fact]
    public void Execute_still_registers_the_server_nav_link()
    {
        // Guards against the layout-banner addition accidentally replacing rather than joining the
        // pre-existing server-nav registration.
        using var provider = BuildProvider();
        var extensions = provider.GetServices<IUIExtension>().ToList();

        Assert.Contains(extensions, e => e.Location == "server-nav" && e.Partial == "SecSwitch/Nav");
    }

    [Fact]
    public void Execute_registers_the_periodic_task_hourly()
    {
        using var provider = BuildProvider();
        var scheduled = provider.GetServices<ScheduledTask>().ToList();

        var task = Assert.Single(scheduled, s => s.PeriodicTaskType == typeof(SecSwitchPeriodicTask));
        Assert.Equal(TimeSpan.FromHours(1), task.Every);
    }

    [Fact]
    public void Execute_registers_an_http_client_factory()
    {
        // Hard requirement 2 (Task 13): AdvisoryFetcher must not be a captured, process-lifetime
        // HttpClient - see SecSwitchPeriodicTask's own doc comment. This only proves
        // IHttpClientFactory itself is resolvable (i.e. SOME AddHttpClient overload was called) and
        // that the named client this plugin actually uses can be created without throwing -
        // IHttpClientFactory.CreateClient never throws for an unregistered name either, so this does
        // NOT by itself prove the specific named registration exists; that line is verified by
        // inspection (SecSwitchPlugin.cs) and by AdvisoryFetcherTests/ScaffoldTests exercising
        // AdvisoryFetcher's real constructor shape elsewhere.
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();

        var client = factory.CreateClient(SecSwitchPeriodicTask.HttpClientName);
        Assert.NotNull(client);
    }
}
