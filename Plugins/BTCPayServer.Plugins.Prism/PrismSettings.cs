using System.Collections.Generic;

namespace BTCPayServer.Plugins.Prism;

public class PrismSettings
{
    public bool Enabled { get; set; }

    public Dictionary<string, long> DestinationBalance { get; set; } = new();
    public Split[] Splits { get; set; }
    public Dictionary<string, PendingPayout> PendingPayouts { get; set; } = new();
    public long SatThreshold { get; set; } = 100;
    public ulong Version { get; set; } = 0;
}