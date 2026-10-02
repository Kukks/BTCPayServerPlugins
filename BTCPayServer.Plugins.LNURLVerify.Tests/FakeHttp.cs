using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

/// <summary>Deterministic HttpMessageHandler: exact-URL routes, then predicate routes, else 404.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public readonly Dictionary<string, (HttpStatusCode Code, string Body)> Routes = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentQueue<string> Requests = new();
    public readonly ConcurrentQueue<string> AcceptHeaders = new();
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, (HttpStatusCode Code, string Body)> Respond)> _handlers = new();

    public FakeHttp Map(string url, string body, HttpStatusCode code = HttpStatusCode.OK)
    { Routes[url] = (code, body); return this; }

    public FakeHttp When(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, (HttpStatusCode Code, string Body)> respond)
    { lock (_handlers) _handlers.Add((match, respond)); return this; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Enqueue(url);
        AcceptHeaders.Enqueue(request.Headers.Accept.ToString());
        if (Routes.TryGetValue(url, out var r)) return Respond(r);
        lock (_handlers)
            foreach (var h in _handlers)
                if (h.Match(request)) return Respond(h.Respond(request));
        return Respond((HttpStatusCode.NotFound, "{}"));
    }

    private static Task<HttpResponseMessage> Respond((HttpStatusCode Code, string Body) r) =>
        Task.FromResult(new HttpResponseMessage(r.Code) { Content = new StringContent(r.Body) });

    public HttpClient Client() => new(this);

    /// <summary>The decoded <c>verify</c> values of a verifyBatch request.</summary>
    public static string[] VerifyParams(HttpRequestMessage request) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query).GetValues("verify") ?? Array.Empty<string>();
}
