using System.Globalization;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionApp : AppBaseType
{
    private readonly LinkGenerator _linkGenerator;
    private readonly IOptions<BTCPayServerOptions> _options;
    public const string AppType = "Subscription";

    public SubscriptionApp(
        LinkGenerator linkGenerator,
        IOptions<BTCPayServerOptions> options)
    {
        Description = "Subscription";
        Type = AppType;
        _linkGenerator = linkGenerator;
        _options = options;
    }

    public override Task<string> ConfigureLink(AppData app)
    {
        return Task.FromResult(_linkGenerator.GetPathByAction(
            nameof(SubscriptionController.Update),
            "Subscription", new {appId = app.Id}, _options.Value.RootPath)!);
    }

    public override Task<object?> GetInfo(AppData appData)
    {
        return Task.FromResult<object?>(null);
    }

    public override Task SetDefaultSettings(AppData appData, string defaultCurrency)
    {
        appData.SetSettings(new SubscriptionAppSettings());
        return Task.CompletedTask;
    }

    public override Task<string> ViewLink(AppData app)
    {
        return Task.FromResult(_linkGenerator.GetPathByAction(nameof(SubscriptionController.View),
            "Subscription", new {appId = app.Id}, _options.Value.RootPath)!);
    }
}