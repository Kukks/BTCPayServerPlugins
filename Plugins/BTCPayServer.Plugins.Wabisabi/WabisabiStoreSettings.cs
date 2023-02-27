using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Client.JsonConverters;
using NBitcoin;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Wabisabi;

public class WabisabiStoreSettings
{
    public List<WabisabiStoreCoordinatorSettings> Settings { get; set; } = new();


    public string MixToOtherWallet { get; set; }
    
    public bool PlebMode { get; set; } = true;
    
    public List<string> InputLabelsAllowed { get; set; } = new();
    public List<string> InputLabelsExcluded { get; set; } = new();
    public bool ConsolidationMode { get; set; } = false;
    public bool RedCoinIsolation { get; set; } = false;
    public int AnonymitySetTarget { get; set; } = 5;

    public bool BatchPayments { get; set; } = true;
    public int ExtraJoinProbability { get; set; } = 0;
    public CrossMixMode CrossMixBetweenCoordinatorsMode { get; set; } = CrossMixMode.WhenFree;
    public int FeeRateMedianTimeFrameHours { get; set; }

    public enum CrossMixMode
    {
        WhenFree,
        Never,
        Always,
    }
}

public class WabisabiStoreCoordinatorSettings
{
    
    public string Coordinator { get; set; }
    public bool Enabled { get; set; } = false;
    public LastCoordinatorRoundConfig RoundWhenEnabled { get; set; }
}

public class LastCoordinatorRoundConfig
{
    
    public decimal CoordinationFeeRate { get; set; }
    public string PlebsDontPayThreshold { get; set; }
    [JsonIgnore]
    public Money PlebsDontPayThresholdM => Money.Parse(PlebsDontPayThreshold);
    
    public int MinInputCountByRound { get; set; }
}
