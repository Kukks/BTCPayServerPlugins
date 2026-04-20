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

    // Re-apply options
    public bool ReapplyLightning { get; set; }
    public bool ReapplyCurrency { get; set; }
    public bool ReapplySpread { get; set; }

    // CSV import result
    public string StatusMessage { get; set; }
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

    // Login code (generated server-side for plugin-created users)
    public string LoginCodeUrl { get; set; }
    public bool HasLoginCode => !string.IsNullOrEmpty(LoginCodeUrl);
    public string PosLink { get; set; }
    public bool IsExistingUser { get; set; }
}
