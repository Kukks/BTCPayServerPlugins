#nullable enable
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.Prism.ViewModels;

public class PrismViewModel
{
    // Global settings
    public bool Enabled { get; set; }
    public long SatThreshold { get; set; } = 100;
    public decimal Reserve { get; set; } = 2;

    // Splits (indexed form binding)
    public List<SplitViewModel> Splits { get; set; } = new();

    // Balances & payouts (display only, not form-bound)
    public Dictionary<string, long> DestinationBalances { get; set; } = new();
    public Dictionary<string, PendingPayout> PendingPayouts { get; set; } = new();

    // Destination aliases (for dropdowns + display)
    public Dictionary<string, PrismDestination> Destinations { get; set; } = new();

    // Display-only data (populated by controller, not bound from form)
    public List<SelectListItem> AvailableStores { get; set; } = new();
    public List<SelectListItem> AvailableLnAddresses { get; set; } = new();
    public List<SelectListItem> AvailableApps { get; set; } = new();
    public Dictionary<string, List<SelectListItem>> AppProducts { get; set; } = new();
    public bool HasLnProcessor { get; set; }
    public bool HasChainProcessor { get; set; }

    // For version tracking (optimistic concurrency)
    public ulong Version { get; set; }
    public string? StoreId { get; set; }
}

public class SplitViewModel
{
    // Encoded source string (populated on load, rebuilt on save from type-specific fields)
    public string? Source { get; set; }

    // Source type selector
    public string SourceType { get; set; } = "catchall";

    // Catch-all fields
    public string CatchAllType { get; set; } = "*All";

    // LN address fields
    public string? LnAddress { get; set; }

    // POS fields
    public string? PosAppId { get; set; }
    public string? PosProductId { get; set; }
    public string? PosPaymentFilter { get; set; }

    // Wallet transfer fields
    public string? TransferPaymentMethod { get; set; }
    public string? TransferAmount { get; set; }
    public string TransferFrequency { get; set; } = "M";
    public string? TransferDay { get; set; }

    // Destinations
    public List<SplitDestinationViewModel> Destinations { get; set; } = new();
}

public class SplitDestinationViewModel
{
    public string? Destination { get; set; }
    public decimal Percentage { get; set; }
}

public class EditDestinationViewModel
{
    public string? Id { get; set; }
    public string? OriginalId { get; set; }
    public string DestinationType { get; set; } = "address";
    public string? AddressValue { get; set; }
    public string? SelectedStoreId { get; set; }
    public string? StorePaymentMethod { get; set; }
    public long? SatThreshold { get; set; }
    public decimal? Reserve { get; set; }
    public string? StoreId { get; set; }

    // Display-only
    public List<SelectListItem> AvailableStores { get; set; } = new();
    public bool IsInUse { get; set; }
}
