using System.Collections.Generic;

namespace BTCPayServer.Plugins.Conference;

public class ConferenceSettings
{
    public string DefaultLightningConnectionString { get; set; }
    public string DefaultCurrency { get; set; } = "USD";
    public decimal DefaultSpread { get; set; }
    public List<ConferenceMerchant> Merchants { get; set; } = new();
}

public class ConferenceMerchant
{
    public string Email { get; set; }
    public string StoreName { get; set; }
    public string Currency { get; set; }
    public decimal? Spread { get; set; }
    public string LightningConnectionString { get; set; }
    public string Password { get; set; }
    public string UserId { get; set; }
    public string StoreId { get; set; }
    public string PosAppId { get; set; }

    public bool IsProvisioned => !string.IsNullOrEmpty(UserId) &&
                                 !string.IsNullOrEmpty(StoreId) &&
                                 !string.IsNullOrEmpty(PosAppId);
}
