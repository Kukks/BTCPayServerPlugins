using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Prism.ViewModel;

public class AutoTransferSettingsViewModel
{
    public bool Enabled { get; set; }

    [Range(0, 100)]
    public int ReserveFeePercentage { get; set; }
    public long SatThreshold { get; set; }

    [Range(546, long.MaxValue)]
    [Display(Name = "Minimum wallet balance required to initiate transfer (in Sats)")]
    public long MinimumBalanceThreshold { get; set; }

    [Display(Name = "Enable Scheduled Transfers")]
    public bool EnableScheduledAutomation { get; set; }
    public string AutomationTransferDays { get; set; }

    [Display(Name = "Available Stores")]
    public List<SelectListItem> AvailableStores { get; set; } = new();
    public string DestinationBatchId { get; set; } = string.Empty;
    public List<AutoTransferDestination> Destinations { get; set; } = new();
    public Dictionary<string, AutoTransferPayout> PendingPayouts { get; set; } = new();
}

public record AutoTransferPayout(long PayoutAmount, long FeeCharged, string DestinationId, string StoreName, DateTimeOffset payoutDate);
