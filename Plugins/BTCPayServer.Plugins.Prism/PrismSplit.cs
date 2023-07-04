namespace BTCPayServer.Plugins.Prism;

public class PrismSplit
{
    public PrismSplit()
    {
        
    }
    public PrismSplit(decimal Percentage, string Destination)
    {
        this.Percentage = Percentage;
        this.Destination = Destination;
    }

    public decimal Percentage { get; set; }
    public string Destination { get; set; }

    public void Deconstruct(out decimal Percentage, out string Destination)
    {
        Percentage = this.Percentage;
        Destination = this.Destination;
    }
}