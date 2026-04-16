using System.Collections.Generic;

namespace BTCPayServer.Plugins.Conference.ViewModels;

public class ConferenceDashboardViewModel
{
    public string AppId { get; set; }
    public string AppName { get; set; }
    public string StoreId { get; set; }
    public ReportTimeRange SelectedTimeRange { get; set; } = ReportTimeRange.Today;
    public List<DashboardMerchantViewModel> Merchants { get; set; } = new();
    public ConferenceReport Report { get; set; }
}

public class DashboardMerchantViewModel
{
    public string Email { get; set; }
    public string StoreName { get; set; }
    public string StoreId { get; set; }
    public string PosAppId { get; set; }
    // UserId intentionally NOT exposed to the view — login codes are generated server-side

    // Live data from store
    public string StoreCurrency { get; set; }
    public decimal Spread { get; set; }

    // Links
    public string StoreLink { get; set; }
    public string PosLink { get; set; }

    // Login code (generated server-side, never expose userId to client)
    public string LoginCodeUrl { get; set; }
    public bool HasLoginCode => !string.IsNullOrEmpty(LoginCodeUrl);

    // Health
    public string Status { get; set; }
    public bool HasError { get; set; }

    // Report data for this merchant
    public MerchantReport MerchantReport { get; set; }
}
