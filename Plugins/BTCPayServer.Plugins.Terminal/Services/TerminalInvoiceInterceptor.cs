using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
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
            if (evt.Name == InvoiceEvent.Created)
            {
                HandleCreated(evt);
                return;
            }

            // Keep serving the invoice while it is awaiting payment (New) or a payment is
            // confirming (Processing) — a 0-conf payment is not treated as done. Once it
            // reaches a final state (settled, expired or invalid) it is finished, so stop
            // serving it and the customer screen returns to "waiting" for the next sale.
            // Keying off the live status (rather than specific event names) covers every
            // settlement path, including manual completion (MarkedCompleted -> Settled).
            var status = evt.Invoice.Status;
            if ((status == InvoiceStatus.Settled || status == InvoiceStatus.Expired || status == InvoiceStatus.Invalid) &&
                _terminalService.ClearInvoice(evt.Invoice.Id))
            {
                _logger.LogInformation("Terminal stopped serving finished invoice {InvoiceId} ({Status})",
                    evt.Invoice.Id, status);
            }
        });
    }

    private void HandleCreated(InvoiceEvent evt)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
            return;

        if (!httpContext.Request.Cookies.TryGetValue(TerminalService.CheckInCookieName(evt.Invoice.StoreId), out var terminalId) ||
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
