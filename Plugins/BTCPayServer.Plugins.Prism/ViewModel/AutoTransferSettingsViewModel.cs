using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Prism.ViewModel;

public class AutoTransferSettingsViewModel
{
    public bool Enabled { get; set; }
    public int ReserveFeePercentage { get; set; }
    public long SatThreshold { get; set; }

    [Display(Name = "Minimum wallet balance required to initiate transfer (in Sats)")]
    public long MinimumBalanceThreshold { get; set; }

    [Display(Name = "Enable Scheduled Transfers")]
    public bool EnableScheduledAutomation { get; set; }
    public string AutomationTransferDays { get; set; }

    [Display(Name = "Available Stores")]
    public List<SelectListItem> AvailableStores { get; set; } = new();

    [Range(0, long.MaxValue)]
    [Display(Name = "Minimum Balance (sats)")]
    public long MinBalanceSats { get; set; }

    [Display(Name = "Transfer History")]
    public List<TransferRecord> TransferHistory { get; set; } = new();
    public string DestinationBatchId { get; set; } = string.Empty;
    public List<AutoTransferDestination> Destinations { get; set; } = new();
    public Dictionary<string, AutoTransferPayout> PendingPayouts { get; set; } = new();
    public Dictionary<string, PoSTemplateAutoTransferViewModel> PoSTransferViewModel { get; set; } = new();
}


public class PoSTemplateAutoTransferViewModel
{
    public string AppId { get; set; }
    public string ProductItemId { get; set; }
    public string ProductTitle { get; set; }
    public decimal Price { get; set; }
    public int Percentage { get; set; }
}

public class TransferRecord
{
    public DateTime Timestamp { get; set; }
    public string FromStore { get; set; } = string.Empty;
    public string ToStore { get; set; } = string.Empty;
    public long AmountSats { get; set; }
    public string Status { get; set; } = string.Empty;
}

public record AutoTransferPayout(long PayoutAmount, long FeeCharged, string DestinationId, string StoreName, DateTimeOffset payoutDate);
