using System.Collections.Generic;

namespace BTCPayServer.Plugins.Prism;

public class Split
{
    public string Source { get; set; }
    
    public string? Schedule { get; set; }
    public List<PrismSplit> Destinations { get; init; } = new();
}