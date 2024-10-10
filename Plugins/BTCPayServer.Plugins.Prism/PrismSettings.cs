using System.Collections.Generic;

namespace BTCPayServer.Plugins.Prism;

public class PrismSettings
{
    public bool Enabled { get; set; }

    public Dictionary<string, long> DestinationBalance { get; set; } = new();
    public List<Split> Splits { get; set; } = new();
    public Dictionary<string, PendingPayout> PendingPayouts { get; set; } = new();
    public Dictionary<string, PrismDestination> Destinations { get; set; } = new();
    public long SatThreshold { get; set; } = 100;
    public ulong Version { get; set; } = 0;
    public decimal Reserve { get; set; } = 2;
}

public class PrismDestination
{
    public string Destination { get; set; }
    public decimal? Reserve { get; set; }
    public long? SatThreshold { get; set; }
    public string? PayoutMethodId { get; set; }
}