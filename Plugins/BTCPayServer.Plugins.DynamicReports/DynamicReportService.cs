using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services;
using BTCPayServer.Services.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.DynamicReports;

public class DynamicReportService:IHostedService
{
    private readonly SettingsRepository _settingsRepository;
    private readonly ReportService _reportService;
    private readonly IServiceProvider _serviceProvider;

    public DynamicReportService(SettingsRepository settingsRepository, ReportService reportService, IServiceProvider serviceProvider)
    {
        _settingsRepository = settingsRepository;
        _reportService = reportService;
        _serviceProvider = serviceProvider;
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await _settingsRepository.GetSettingAsync<DynamicReportsSettings>();
        if (result?.Reports?.Any() is true)
        {
            foreach (var report in result.Reports)
            {
                var reportProvider = ActivatorUtilities.CreateInstance<PostgresReportProvider>(_serviceProvider);
                reportProvider.Setting = report.Value;
                reportProvider.ReportName = report.Key;
                _reportService.ReportProviders.TryAdd(report.Key, reportProvider);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
    }
    
    

    public async Task UpdateDynamicReport(string name, DynamicReportsSettings.DynamicReportSetting setting)
    {
        _reportService.ReportProviders.TryGetValue(name, out var report);
        if (report is not null && report is not PostgresReportProvider)
        {
            throw new InvalidOperationException("Only PostgresReportProvider can be updated dynamically");
        }

        var result = await _settingsRepository.GetSettingAsync<DynamicReportsSettings>() ?? new DynamicReportsSettings();
        if (report is PostgresReportProvider postgresReportProvider)
        {
            if (setting is null)
            {
                //remove report
                _reportService.ReportProviders.Remove(name);

                result.Reports.Remove(name);
                await _settingsRepository.UpdateSetting(result);
            }
            else
            {
                postgresReportProvider.Setting = setting;
                result.Reports[name] = setting;
                postgresReportProvider.ReportName = name;
                await _settingsRepository.UpdateSetting(result);
            }
        }
        else if (setting is not null)
        {
            var reportProvider = ActivatorUtilities.CreateInstance<PostgresReportProvider>(_serviceProvider);
            reportProvider.Setting = setting;

            reportProvider.ReportName = name;
            result.Reports[name] = setting;
            await _settingsRepository.UpdateSetting(result);
            _reportService.ReportProviders.TryAdd(name, reportProvider);
        }
    }
}