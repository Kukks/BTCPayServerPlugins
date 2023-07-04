using System.Collections.Generic;

namespace BTCPayServer.Plugins.Prism;

public class Split
{
    public Split()
    {
        
    }
    public Split(string Source, List<PrismSplit> Destinations)
    {
        this.Source = Source;
        this.Destinations = Destinations;
    }

    public string Source { get; set; }
    public List<PrismSplit> Destinations { get; init; } = new();

    public void Deconstruct(out string Source, out List<PrismSplit> Destinations)
    {
        Source = this.Source;
        Destinations = this.Destinations;
    }
}