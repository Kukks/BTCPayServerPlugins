using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.Prism.ViewModel;

namespace BTCPayServer.Plugins.Prism;

public class AutoTransferSettings
{
    public bool Enabled { get; set; }
    public int Reserve { get; set; } = 2;
    public long SatThreshold { get; set; } = 100;
    public bool EnableScheduledAutomation { get; set; }
    public string AutomationTransferDays { get; set; }
    public Dictionary<string, List<AutoTransferDestination>> ScheduledDestinations { get; set; } = new();
    public List<AutoTransferHistory> PaymentRecord { get; set; } = new();
    public Dictionary<string, AutoTransferPayout> PendingPayouts { get; set; } = new();
}


public class AutoTransferDestination
{
    public string StoreId { get; set; }
    public long Amount { get; set; }
    public string DestinationPaymentMethod { get; set; }
    public string SourcePaymentMethod { get; set; }
}

public class AutoTransferHistory
{
    public string Id { get; set; }
    public string StoreId { get; set; }
    public string Destination { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    public string PayoutMethodId { get; set; }
}