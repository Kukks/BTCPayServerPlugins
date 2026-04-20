using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Conference;

public class ConferenceReportService
{
    private readonly InvoiceRepository _invoiceRepository;

    public ConferenceReportService(InvoiceRepository invoiceRepository)
    {
        _invoiceRepository = invoiceRepository;
    }

    public async Task<ConferenceReport> GenerateReport(
        List<ConferenceMerchant> merchants,
        ReportTimeRange timeRange,
        CancellationToken cancellationToken = default)
    {
        var (startDate, endDate) = GetDateRange(timeRange);
        var storeIds = merchants
            .Where(m => !string.IsNullOrEmpty(m.StoreId))
            .Select(m => m.StoreId)
            .ToArray();

        if (storeIds.Length == 0)
        {
            return new ConferenceReport
            {
                TimeRange = timeRange,
                StartDate = startDate,
                EndDate = endDate
            };
        }

        // Single query for all stores
        var invoices = await _invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = storeIds,
            StartDate = startDate,
            EndDate = endDate,
            Status = ["Settled", "Processing"],
            IncludeArchived = false
        }, cancellationToken);

        var report = new ConferenceReport
        {
            TimeRange = timeRange,
            StartDate = startDate,
            EndDate = endDate
        };

        // Group invoices by store
        var invoicesByStore = invoices.GroupBy(i => i.StoreId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var merchant in merchants)
        {
            if (string.IsNullOrEmpty(merchant.StoreId))
                continue;

            var merchantInvoices = invoicesByStore.GetValueOrDefault(merchant.StoreId) ?? new List<InvoiceEntity>();

            var merchantReport = new MerchantReport
            {
                Email = merchant.Email,
                StoreName = merchant.StoreName,
                StoreId = merchant.StoreId,
                InvoiceCount = merchantInvoices.Count
            };

            foreach (var invoice in merchantInvoices)
            {
                merchantReport.TotalStoreCurrency += invoice.Price;
                merchantReport.StoreCurrency = invoice.Currency;

                foreach (var payment in invoice.GetPayments(true))
                {
                    var paymentCurrency = payment.Currency;
                    if (!merchantReport.PaymentBreakdown.TryGetValue(paymentCurrency, out var summary))
                    {
                        summary = new PaymentSummary { Currency = paymentCurrency };
                        merchantReport.PaymentBreakdown[paymentCurrency] = summary;
                    }

                    summary.TotalAmount += payment.PaidAmount.Gross;
                }
            }

            report.MerchantReports.Add(merchantReport);
        }

        // Aggregate totals
        foreach (var mr in report.MerchantReports)
        {
            if (!string.IsNullOrEmpty(mr.StoreCurrency))
            {
                report.TotalsByStoreCurrency.TryGetValue(mr.StoreCurrency, out var existing);
                report.TotalsByStoreCurrency[mr.StoreCurrency] = existing + mr.TotalStoreCurrency;
            }

            report.TotalInvoices += mr.InvoiceCount;
        }

        return report;
    }

    private static (DateTimeOffset start, DateTimeOffset end) GetDateRange(ReportTimeRange range)
    {
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);

        return range switch
        {
            ReportTimeRange.Today => (todayStart, now),
            ReportTimeRange.Yesterday => (todayStart.AddDays(-1), todayStart),
            ReportTimeRange.Last7Days => (todayStart.AddDays(-7), now),
            _ => (todayStart, now)
        };
    }
}

public enum ReportTimeRange
{
    Today,
    Yesterday,
    Last7Days
}

public class ConferenceReport
{
    public ReportTimeRange TimeRange { get; set; }
    public DateTimeOffset StartDate { get; set; }
    public DateTimeOffset EndDate { get; set; }
    public List<MerchantReport> MerchantReports { get; set; } = new();
    public Dictionary<string, decimal> TotalsByStoreCurrency { get; set; } = new();
    public int TotalInvoices { get; set; }
}

public class MerchantReport
{
    public string Email { get; set; }
    public string StoreName { get; set; }
    public string StoreId { get; set; }
    public string StoreCurrency { get; set; }
    public decimal TotalStoreCurrency { get; set; }
    public Dictionary<string, PaymentSummary> PaymentBreakdown { get; set; } = new();
    public int InvoiceCount { get; set; }
}

public class PaymentSummary
{
    public string Currency { get; set; }
    public decimal TotalAmount { get; set; }
}
