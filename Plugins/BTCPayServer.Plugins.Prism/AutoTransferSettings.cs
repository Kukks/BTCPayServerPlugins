using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.Prism.ViewModel;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Prism;

public class AutoTransferSettings
{
    public bool Enabled { get; set; }
    public int Reserve { get; set; } = 2;
    public long SatThreshold { get; set; } = 100;
    public long MinimumBalanceThreshold { get; set; }
    public bool EnableScheduledAutomation { get; set; }
    public string AutomationTransferDays { get; set; }
    public Dictionary<string, List<AutoTransferDestination>> ScheduledDestinations { get; set; } = new();
    public Dictionary<string, AutoTransferPayout> PendingPayouts { get; set; } = new();
    public List<PosAppProductSplitModel> PosProductAutoTransferSplit { get; set; } = new();
}


public class AutoTransferDestination
{
    [Required]
    public string StoreId { get; set; }
    [Range(0, long.MaxValue)]
    public long Amount { get; set; }
}

public class PosAppProductSplitModel
{
    public string AppId { get; set; }
    public string AppTitle { get; set; }
    public List<ProductSplitItemModel> Products { get; set; } = new();
}

public class ProductSplitItemModel
{
    public string ProductId { get; set; }
    public string Title { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; }
    public int Percentage { get; set; }
    public string DestinationStoreId { get; set; }
    public List<SelectListItem> StoreOptions { get; set; } = new();
}

