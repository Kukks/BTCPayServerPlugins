#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>Anything the shared poller checks through a LUD-21 verify URL.</summary>
public interface IVerifyTarget
{
    string VerifyUrl { get; }
    string? VerifyBatch { get; }
    DateTimeOffset ExpiresAt { get; }
}

/// <param name="ExpiresAt">The invoice's monitoring expiration, so a late payment is still recorded as core does on-chain.</param>
public sealed record TrackedDestination(
    string InvoiceId, string PaymentMethodId, string Destination, string VerifyUrl, string? VerifyBatch,
    long AmountMsat, DateTimeOffset ExpiresAt) : IVerifyTarget;

public static class TrackedDestinationRegistry
{
    private static readonly ConcurrentDictionary<string, TrackedDestination> _byVerifyUrl = new();

    /// <summary>Fires once per destination, with its payment reference; the destination is no longer tracked.</summary>
    public static event Action<TrackedDestination, string>? Settled;
    /// <summary>The service no longer knows the destination, so its settlement can never be learnt.</summary>
    public static event Action<TrackedDestination, string>? Forgotten;

    public static void Add(TrackedDestination d) => _byVerifyUrl[d.VerifyUrl] = d;
    public static bool Remove(string verifyUrl) => _byVerifyUrl.TryRemove(verifyUrl, out _);
    public static bool IsTracked(string verifyUrl) => _byVerifyUrl.ContainsKey(verifyUrl);
    public static IReadOnlyCollection<TrackedDestination> All() => _byVerifyUrl.Values.ToArray();

    public static void Apply(TrackedDestination d, JObject verify)
    {
        if (string.Equals(verify["status"]?.Value<string>(), "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            if (Remove(d.VerifyUrl)) Forgotten?.Invoke(d, verify["reason"]?.Value<string>() ?? "unknown verify URL");
            return;
        }
        if (verify["settled"]?.Type != JTokenType.Boolean || !verify["settled"]!.Value<bool>()) return;
        // Settled with no reference stays tracked: there is nothing to record or deduplicate it under.
        if (verify["paymentReference"]?.Type != JTokenType.String ||
            verify["paymentReference"]!.Value<string>() is not { Length: > 0 } reference)
            return;
        if (Remove(d.VerifyUrl)) Settled?.Invoke(d, reference);
    }
}
