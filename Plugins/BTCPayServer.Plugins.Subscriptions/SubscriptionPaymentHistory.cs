using System;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionPaymentHistory
{
    
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTime PeriodStart { get; set; }
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTime PeriodEnd { get; set; }
    public string PaymentRequestId { get; set; }
    public bool Settled { get; set; }
}