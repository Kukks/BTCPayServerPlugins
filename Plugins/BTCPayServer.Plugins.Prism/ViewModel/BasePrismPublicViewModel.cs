using BTCPayServer.Models;

namespace BTCPayServer.Plugins.Prism.ViewModel;

public class BasePrismPublicViewModel
{
    public string StoreId { get; set; }
    public string StoreName { get; set; }
    public StoreBrandingViewModel StoreBranding { get; set; }
}
