using System;
using System.Collections.Generic;
using BTCPayServer.JsonConverters;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Subscriptions;

public class SubscriptionAppSettings
{
    [JsonIgnore] public string SubscriptionName { get; set; }
    public string Description { get; set; }
    public int Duration { get; set; }
    
    [JsonConverter(typeof(StringEnumConverter))]
    public DurationType DurationType { get; set; }
    public string? FormId { get; set; }
    [JsonConverter(typeof(NumericStringJsonConverter))]
    public decimal Price { get; set; }
    public string Currency { get; set; }
    public Dictionary<string, Subscription> Subscriptions { get; set; } = new();
    
}

public static class SubscriptionAppSettingsExtensions
{
    
    public static string GetSubscriptionHumanReadableLength(this SubscriptionAppSettings settings)
    {
        return settings.DurationType switch
        {
            DurationType.Day => $"{settings.Duration} day{(settings.Duration > 1 ? "s" : "")}",
            DurationType.Month => $"{settings.Duration} month{(settings.Duration > 1 ? "s" : "")}",
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    public static DateTimeOffset GetNextRenewalDate(this SubscriptionAppSettings settings, DateTimeOffset? lastRenewalDate)
    {
        if (lastRenewalDate == null)
        {
            return DateTimeOffset.UtcNow;
        }
        return settings.DurationType switch
        {
            DurationType.Day => lastRenewalDate.Value.AddDays(settings.Duration),
            DurationType.Month => lastRenewalDate.Value.AddMonths(settings.Duration),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}

public enum DurationType
{
    Day,
    Month
}