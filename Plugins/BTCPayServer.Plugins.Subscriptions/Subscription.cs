using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Subscriptions;

public class Subscription


{
    public string Email { get; set; }
    
    [JsonConverter(typeof(StringEnumConverter))]
    public SubscriptionStatus Status { get; set; }
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset Start { get; set; }
    public List<SubscriptionPaymentHistory> Payments { get; set; }
}