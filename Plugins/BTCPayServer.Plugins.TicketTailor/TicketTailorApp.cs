using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorApp : AppBaseType
{
    private readonly LinkGenerator _linkGenerator;
    private readonly IOptions<BTCPayServerOptions> _options;
    public const string AppType = "TicketTailor";

    public TicketTailorApp(
        LinkGenerator linkGenerator,
        IOptions<BTCPayServerOptions> options)
    {
        Description = "Ticket Tailor";
        Type = AppType;
        _linkGenerator = linkGenerator;
        _options = options;
    }

    public override Task<string> ConfigureLink(AppData app)
    {
        return Task.FromResult(_linkGenerator.GetPathByAction(
            nameof(TicketTailorController.UpdateTicketTailorSettings),
            "TicketTailor", new {appId = app.Id}, _options.Value.RootPath)!);
    }

    public override Task<object?> GetInfo(AppData appData)
    {
        return Task.FromResult<object?>(null);
    }

    public override Task SetDefaultSettings(AppData appData, string defaultCurrency)
    {
        appData.SetSettings(new TicketTailorSettings());
        return Task.CompletedTask;
    }

    public override Task<string> ViewLink(AppData app)
    {
        return Task.FromResult(_linkGenerator.GetPathByAction(nameof(TicketTailorController.View),
            "TicketTailor", new {appId = app.Id}, _options.Value.RootPath)!);
    }
}