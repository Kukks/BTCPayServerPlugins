using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Conference.ViewModels;

public class ConferenceSettingsViewModel
{
    public string AppId { get; set; }
    public string AppName { get; set; }

    [Display(Name = "Default Lightning Connection String")]
    public string DefaultLightningConnectionString { get; set; }

    [Display(Name = "Default Currency")]
    [Required]
    public string DefaultCurrency { get; set; } = "USD";

    [Display(Name = "Default Spread (%)")]
    [Range(0, 100)]
    public decimal DefaultSpread { get; set; }

    public List<MerchantViewModel> Merchants { get; set; } = new();

    // Dashboard / report data (shown in Merchants tab)
    public ReportTimeRange SelectedTimeRange { get; set; } = ReportTimeRange.Today;
    public ConferenceReport Report { get; set; }
}

public class MerchantViewModel
{
    [Required]
    [EmailAddress]
    public string Email { get; set; }

    [Required]
    [Display(Name = "Store Name")]
    public string StoreName { get; set; }

    public string Currency { get; set; }

    [Range(0, 100)]
    public decimal? Spread { get; set; }

    [Display(Name = "Lightning Connection String")]
    public string LightningConnectionString { get; set; }

    public string Password { get; set; }

    // Read-only status info (populated from provisioning state)
    public string UserId { get; set; }
    public string StoreId { get; set; }
    public string PosAppId { get; set; }
    public string Status { get; set; }
    public bool IsProvisioned { get; set; }
    public bool HasError { get; set; }

    public string PosLink { get; set; }
    public bool IsExistingUser { get; set; }
    public bool CanGenerateLoginCode { get; set; }

    // Report data for this merchant
    public MerchantReport MerchantReport { get; set; }
}
