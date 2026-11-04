using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public sealed record FeedResult(IReadOnlyList<Advisory> Advisories, IReadOnlyList<SkippedEntry> Skipped);

public static class AdvisoryFeed
{
    public const int MaxFeedBytes = 1024 * 1024;
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public static bool IsSupportedFeedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <remarks>
    /// The client must not follow redirects: a 3xx is treated as a failed fetch. The timeout covers reading the
    /// body as well, which HttpClient.Timeout does not once headers have arrived; a drip-fed body would otherwise
    /// stall the periodic task and with it every later poll.
    /// </remarks>
    public static async Task<FeedResult> FetchAsync(HttpClient http, string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!IsSupportedFeedUrl(url))
            throw new ArgumentException("The advisory feed URL must be an absolute https:// URL.", nameof(url));

        var limit = timeout ?? FetchTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(limit);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MaxFeedBytes, timeoutSource.Token);

            var advisories = AdvisoryParser.ParseFeed(await response.Content.ReadAsByteArrayAsync(timeoutSource.Token), out var skipped);
            return new FeedResult(advisories, skipped);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The advisory feed did not answer within {limit.TotalSeconds:0} seconds.");
        }
    }
}
