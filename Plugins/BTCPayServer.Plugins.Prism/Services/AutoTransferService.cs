using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.Prism.Services;

public class AutoTransferService : EventHostedServiceBase, IPeriodicTask
{
    private readonly SatBreaker _satBreaker;
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<AutoTransferService> _logger;
    public event EventHandler<AutoTransferPaymentEvent> AutoTransferUpdated;

    public AutoTransferService(SatBreaker satBreaker, EventAggregator eventAggregator,
        ILogger<AutoTransferService> logger) : base(eventAggregator, logger)
    {
        _logger = logger;
        _satBreaker = satBreaker;
        _eventAggregator = eventAggregator;
    }

    public async Task Do(CancellationToken cancellationToken)
    {
        try
        {
            var prismSettings = await _satBreaker.GetAllPrismSettings();
            foreach (var setting in prismSettings)
            {
                if (setting.Value != null)
                {
                    if (!setting.Value.Enabled || !setting.Value.EnableScheduledAutomation) continue;

                    PushEvent(new AutoTransferPaymentEvent { StoreId = setting.Key, Settings = setting.Value });
                }
            }
        }
        catch (PostgresException)
        {
            Logs.PayServer.LogInformation("Skipping task: An error occured.");
        }
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is AutoTransferPaymentEvent sequentialExecute) await HandleSchedulePayment(sequentialExecute.StoreId);

        await base.ProcessEvent(evt, cancellationToken);
    }

    private async Task HandleSchedulePayment(string storeId)
    {
        var settings = await _satBreaker.Get(storeId);
        if (!settings.Enabled || !settings.EnableScheduledAutomation) return;

        int today = DateTime.UtcNow.Day;
        List<PrismDestination> scheduleTransferDueToday = settings.Destinations.Values.Where(d =>
                d.Destination?.StartsWith("prism-store:", StringComparison.OrdinalIgnoreCase) == true &&
                d.Amount.HasValue && d.Amount.Value > settings.SatThreshold &&
                (d.Schedule ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(s => int.TryParse(s, out var day) && day == today)).ToList();

        if (!scheduleTransferDueToday.Any()) return;

        await _satBreaker.StoreAutoTransferPayout(storeId, settings, scheduleTransferDueToday);
    }

    public class AutoTransferPaymentEvent
    {
        public string StoreId { get; set; }
        public PrismSettings Settings { get; set; }
    }
}