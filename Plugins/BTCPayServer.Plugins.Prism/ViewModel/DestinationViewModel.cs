namespace BTCPayServer.Plugins.Prism.ViewModel;

public class DestinationViewModel
{
    public string StoreId { get; set; }
    public string DestinationId { get; set; }
    public string Destination { get; set; }
    public decimal? Reserve { get; set; }
    public long? SatThreshold { get; set; }
    public string? PayoutMethodId { get; set; }
}

