using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Terminal.Services;

public class TerminalInvoiceInterceptor : IHostedService
{
    private readonly EventAggregator _eventAggregator;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TerminalService _terminalService;
    private readonly AppService _appService;
    private readonly ILogger<TerminalInvoiceInterceptor> _logger;
    private IEventAggregatorSubscription _subscription;

    public TerminalInvoiceInterceptor(
        EventAggregator eventAggregator,
        IHttpContextAccessor httpContextAccessor,
        TerminalService terminalService,
        AppService appService,
        ILogger<TerminalInvoiceInterceptor> logger)
    {
        _eventAggregator = eventAggregator;
        _httpContextAccessor = httpContextAccessor;
        _terminalService = terminalService;
        _appService = appService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadTerminalApps();

        _subscription = _eventAggregator.Subscribe<InvoiceEvent>((sub, evt) =>
        {
            switch (evt.Name)
            {
                case InvoiceEvent.Created:
                    HandleCreated(evt);
                    break;
                case InvoiceEvent.Completed:
                case InvoiceEvent.Expired:
                case InvoiceEvent.MarkedInvalid:
                case InvoiceEvent.FailedToConfirm:
                case InvoiceEvent.ExpiredPaidPartial:
                    if (_terminalService.ClearInvoice(evt.Invoice.Id))
                        _logger.LogInformation("Cleared terminal mapping for finalized invoice {InvoiceId}", evt.Invoice.Id);
                    break;
            }
        });
    }

    private void HandleCreated(InvoiceEvent evt)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
            return;

        if (!httpContext.Request.Cookies.TryGetValue("btcpay-terminal", out var terminalId) ||
            string.IsNullOrEmpty(terminalId))
            return;

        var terminal = _terminalService.GetTerminal(terminalId);
        if (terminal == null)
            return;

        if (terminal.StoreId != evt.Invoice.StoreId)
            return;

        _terminalService.SetCurrentInvoice(terminalId, evt.Invoice.Id);
        _logger.LogInformation("Terminal {TerminalId} ({Name}) mapped to invoice {InvoiceId}",
            terminalId, terminal.Name, evt.Invoice.Id);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task LoadTerminalApps()
    {
        try
        {
            var apps = await _appService.GetApps(TerminalApp.AppType);
            foreach (var app in apps)
            {
                var settings = app.GetSettings<TerminalSettings>();
                if (settings?.Terminals?.Count > 0)
                {
                    _terminalService.RegisterTerminals(app.Id, app.StoreDataId, settings.Terminals);
                    _logger.LogInformation("Loaded {Count} terminals for app {AppId}",
                        settings.Terminals.Count, app.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load terminal apps on startup");
        }
    }
}
