using System.Collections.Generic;
using BTCPayServer.JsonConverters;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionAppSettings
{
    [JsonIgnore] public string SubscriptionName { get; set; }
    public string Description { get; set; }
    public int DurationDays { get; set; }
    public string? FormId { get; set; }
    [JsonConverter(typeof(NumericStringJsonConverter))]
    public decimal Price { get; set; }
    public string Currency { get; set; }
    public Dictionary<string, Subscription> Subscriptions { get; set; } = new();
}