using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Services;
using BTCPayServer.Services.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.DynamicReports;
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("~/plugins/dynamicreports")]
public class DynamicReportsController : Controller
{
    private readonly DynamicReportService _dynamicReportService;
    private readonly ApplicationDbContextFactory _dbContextFactory;
    private readonly ReportService _reportService;
    private readonly IScopeProvider _scopeProvider;

    public DynamicReportsController(ReportService reportService, 
        IScopeProvider scopeProvider, 
        DynamicReportService dynamicReportService, ApplicationDbContextFactory dbContextFactory)
    {
        _dynamicReportService = dynamicReportService;
        _dbContextFactory = dbContextFactory;
        _reportService = reportService;
        _scopeProvider = scopeProvider;
    }
    
    [HttpGet("update")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public IActionResult Update(
        string? reportName, string? viewName)
    {

        if (!string.IsNullOrEmpty(viewName) && _reportService.ReportProviders.TryGetValue(viewName, out var vnReport) &&
            vnReport is PostgresReportProvider)
        {
            return RedirectToAction(nameof(Update), new {reportName = viewName});

        }

        if (reportName is null) return View(new DynamicReportViewModel());
        
        if (!_reportService.ReportProviders.TryGetValue(reportName, out var report))
        {
            return NotFound();
        }

        if (report is not PostgresReportProvider postgresReportProvider)
        {
            return NotFound();
        }

        return View(new DynamicReportViewModel()
        {
            Name = reportName,
            Sql = postgresReportProvider.Setting.Sql,
            AllowForNonAdmins = postgresReportProvider.Setting.AllowForNonAdmins
        });

    }  
    [HttpPost("update")]
    [Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Update(
        string reportName, DynamicReportViewModel vm, string command)
    {
        ModelState.Clear();
        if (command == "remove" && reportName is not null)
        {
            await _dynamicReportService.UpdateDynamicReport(reportName, null);
            TempData[WellKnownTempData.SuccessMessage] = $"Report {reportName} removed";
            return RedirectToAction(nameof(Update));
        }
        
        if (command == "test")
        {
            if(string.IsNullOrEmpty(vm.Sql))
            {
                ModelState.AddModelError(nameof(vm.Sql), "SQL is required");
                return View(vm);
            }
            try
            {
                var context = new QueryContext(_scopeProvider.GetCurrentStoreId(), DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue);
                await PostgresReportProvider.ExecuteQuery(_dbContextFactory, context, vm.Sql, CancellationToken.None);   
                
                TempData["Data"] = JsonConvert.SerializeObject(context);
                TempData[WellKnownTempData.SuccessMessage] =  $"Fetched {context.Data.Count} rows with {context.ViewDefinition?.Fields.Count} columns";
                
            }
            catch (Exception e)
            {
                ModelState.AddModelError(nameof(vm.Sql), "Could not execute SQL: " + e.Message);
            }
            
            
            return View(vm);
        }
        
        string msg = null;
        if(string.IsNullOrEmpty(vm.Sql))
        {
            ModelState.AddModelError(nameof(vm.Sql), "SQL is required");
        }
        else
        {
            try
            {
                var context = new QueryContext(_scopeProvider.GetCurrentStoreId(), DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue);
                await PostgresReportProvider.ExecuteQuery(_dbContextFactory, context, vm.Sql, CancellationToken.None);   
                msg = $"Fetched {context.Data.Count} rows with {context.ViewDefinition?.Fields.Count} columns";
                TempData["Data"] = JsonConvert.SerializeObject(context);
            }
            catch (Exception e)
            {
                ModelState.AddModelError(nameof(vm.Sql), "Could not execute SQL: " + e.Message);
            }
        }
        if(string.IsNullOrEmpty(vm.Name))
        {
            ModelState.AddModelError(nameof(vm.Name), "Name is required");
        }
        if (!ModelState.IsValid)
        {
            return View(vm);
        }

        await _dynamicReportService.UpdateDynamicReport(reportName??vm.Name, vm);
        TempData.SetStatusMessageModel(new StatusMessageModel()
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Html = $"Report {reportName} {(reportName is null ? "created" : "updated")}{(msg is null? string.Empty: $"<br/>{msg})")}"
        });
        TempData[WellKnownTempData.SuccessMessage] = $"Report {reportName} {(reportName is null ? "created" : "updated")}";
       
        return RedirectToAction(nameof(Update) , new {reportName = reportName??vm.Name});
        
    }
}