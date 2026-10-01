#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LNURLVerify;

public enum BatchOutcomeKind { Ok, TooLong, Unsupported, Failed }

/// <param name="Results">For <see cref="BatchOutcomeKind.Ok"/>: each item keyed by the exact verify URL it answers.</param>
public sealed record BatchOutcome(BatchOutcomeKind Kind, IReadOnlyDictionary<string, JObject>? Results = null, string? Error = null);

/// <summary>The one-shot form of LUD-XX <c>verifyBatch</c>: one GET answering many LUD-21 verify URLs.</summary>
public static class VerifyBatchClient
{
    public static Uri BuildUri(string endpoint, IEnumerable<string> verifyUrls)
    {
        var builder = new UriBuilder(endpoint);
        var q = new StringBuilder(builder.Query.TrimStart('?'));
        foreach (var url in verifyUrls)
        {
            if (q.Length > 0) q.Append('&');
            q.Append("verify=").Append(Uri.EscapeDataString(url));
        }
        builder.Query = q.ToString();
        return builder.Uri;
    }

    public static async Task<BatchOutcome> Fetch(HttpClient http, string endpoint, IReadOnlyCollection<string> verifyUrls,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(endpoint, verifyUrls));
        // Never text/event-stream: that selects the streamed form, which this client does not speak.
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return new BatchOutcome(BatchOutcomeKind.Failed, Error: e.Message); }

        using (response)
        {
            switch ((int)response.StatusCode)
            {
                case 414:
                case 431:
                    return new BatchOutcome(BatchOutcomeKind.TooLong);
                case 404:
                case 405:
                case 501:
                    return new BatchOutcome(BatchOutcomeKind.Unsupported);
            }
            if (!response.IsSuccessStatusCode)
                return new BatchOutcome(BatchOutcomeKind.Failed, Error: $"HTTP {(int)response.StatusCode}");

            JObject json;
            try { json = JObject.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (Exception e) { return new BatchOutcome(BatchOutcomeKind.Failed, Error: e.Message); }
            if (!string.Equals(json["status"]?.Value<string>(), "OK", StringComparison.OrdinalIgnoreCase) ||
                json["results"] is not JObject results)
                return new BatchOutcome(BatchOutcomeKind.Failed, Error: json["reason"]?.Value<string>() ?? "malformed verifyBatch response");

            var items = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var p in results.Properties())
                if (p.Value is JObject item)
                    items[p.Name] = item;
            // Keys must echo the request byte for byte; a server that rewrites them can never be matched.
            if (!verifyUrls.Any(items.ContainsKey))
                return new BatchOutcome(BatchOutcomeKind.Unsupported);
            return new BatchOutcome(BatchOutcomeKind.Ok, items);
        }
    }
}
