using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using WalletWasabi.Wallets;

namespace BTCPayServer.Plugins.Wabisabi;

public class WabisabiStoreSettings
{
    public List<WabisabiStoreCoordinatorSettings> Settings { get; set; } = new();
    public bool Active { get; set; } = true;

    public string MixToOtherWallet { get; set; }
    
    public bool PlebMode { get; set; } = true;
    
    public List<string> InputLabelsAllowed { get; set; } = new();
    public List<string> InputLabelsExcluded { get; set; } = new();
    
    [JsonConverter(typeof(ConsolidationModeTypeJsonConverter))]
    public ConsolidationModeType ConsolidationMode { get; set; } = ConsolidationModeType.WhenLowFeeAndManyUTXO;
    public bool RedCoinIsolation { get; set; } = false;
    public int AnonymitySetTarget { get; set; } = 5;

    public bool BatchPayments { get; set; } = true;
    public bool ParanoidPayments { get; set; } = false;
    public int ExtraJoinProbability { get; set; } = 0;
    public CrossMixMode CrossMixBetweenCoordinatorsMode { get; set; } = CrossMixMode.WhenFree;
    public int FeeRateMedianTimeFrameHours { get; set; }
    public long MinimumDenominationAmount { get; set; } = 10000;
    public long[] AllowedDenominations { get; set; }
    public int ExplicitHighestFeeTarget { get; set; } = BTCPayWallet.DefaultExplicitHighestFeeTarget;
    public int LowFeeTarget { get; set; } = BTCPayWallet.DefaultLowFeeTarget;

    public bool ConsiderEntryProximity { get; set; } = true;

    public enum CrossMixMode
    {
        WhenFree,
        Never,
        Always,
    }
}


public class ConsolidationModeTypeJsonConverter: JsonConverter<ConsolidationModeType>
{
    private readonly StringEnumConverter _converter;

    public ConsolidationModeTypeJsonConverter()
    {
        _converter = new StringEnumConverter();
    }

    public override void WriteJson(JsonWriter writer, ConsolidationModeType value, JsonSerializer serializer)
    {
        _converter.WriteJson(writer, value, serializer);
    }

    public override ConsolidationModeType ReadJson(JsonReader reader, Type objectType, ConsolidationModeType existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        return reader switch
        {
            {TokenType: JsonToken.Boolean, Value: true} => ConsolidationModeType.Always,
            {TokenType: JsonToken.Boolean} => ConsolidationModeType.Never,
            _ => (ConsolidationModeType) _converter.ReadJson(reader, objectType, existingValue, serializer)!
        };
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
    
    public int MinInputCountByRound { get; set; }
}
