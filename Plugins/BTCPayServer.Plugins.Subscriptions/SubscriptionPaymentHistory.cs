using System;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionPaymentHistory
{
    
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset PeriodStart { get; set; }
    [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
    public DateTimeOffset PeriodEnd { get; set; }
    public string PaymentRequestId { get; set; }
    public bool Settled { get; set; }
}