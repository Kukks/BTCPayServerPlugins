using System.Net;
using System.Text;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// <summary>
/// A fake HTTP feed for AdvisoryFetcherTests. Deliberate deviations from a fake that just echoes
/// the requested route, all existing to exercise AdvisoryFetcher's hardening rather than only its
/// happy path:
///
/// 1. Every response's Content-Length header is stripped after construction, UNLESS its path is
///    listed in <paramref name="honestContentLengthRoutes"/>. AdvisoryFetcher's byte-size caps are
///    enforced two ways: a Content-Length fast path that rejects an honestly-oversized response
///    before reading any of its body, and a running-total check while streaming that catches a
///    response which lies about or omits Content-Length. Stripping the header by default forces
///    every test in this file - not just a specially crafted one - to exercise the streaming
///    check, which is the one that has to hold against a genuinely hostile server (a real one has
///    no obligation to send an honest, or any, Content-Length). `honestContentLengthRoutes` exists
///    so a test can specifically exercise the OTHER path - the fast one - since nothing else in
///    this file ever does (Task 8 review, Minor 8).
/// 2. <paramref name="hangingRoutes"/> lets a test simulate a slow-loris response that never
///    completes on its own: SendAsync awaits indefinitely on the request's own CancellationToken
///    for those paths, so the request only ever ends when that token is cancelled - by
///    AdvisoryFetcher's own per-request or overall-poll timeout, in the tests that use this.
/// 3. <paramref name="redirectedFinalUri"/> simulates what a REAL redirect-following HttpClient
///    handler leaves behind for a 3xx response it transparently followed: a response whose
///    RequestMessage.RequestUri names a DIFFERENT URL than the one actually requested.
///    HttpMessageHandler (what this class derives from) has no redirect-following logic of its
///    own, so without this a test literally cannot produce that mismatch - AdvisoryFetcher's
///    post-response containment check (Task 8 review, Finding 2) would otherwise go untested.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    readonly Dictionary<string, string> _routes;
    readonly HashSet<string> _hangingRoutes;
    readonly HashSet<string> _honestContentLengthRoutes;
    readonly Dictionary<string, string> _redirectedFinalUri;
    public List<string> Requested { get; } = [];

    public FakeHttp(
        Dictionary<string, string> routes,
        IEnumerable<string>? hangingRoutes = null,
        IEnumerable<string>? honestContentLengthRoutes = null,
        Dictionary<string, string>? redirectedFinalUri = null)
    {
        _routes = routes;
        _hangingRoutes = new HashSet<string>(hangingRoutes ?? []);
        _honestContentLengthRoutes = new HashSet<string>(honestContentLengthRoutes ?? []);
        _redirectedFinalUri = redirectedFinalUri ?? new Dictionary<string, string>();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.ToString();
        Requested.Add(path);

        if (_hangingRoutes.Contains(path))
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // Never returns on its own - see class doc comment.

        if (!_routes.TryGetValue(path, out var body))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
        if (!_honestContentLengthRoutes.Contains(path))
            response.Content.Headers.ContentLength = null;

        // Explicit in both branches, deliberately - never left for HttpClient's own machinery to
        // fill in, so the fake's behaviour is fully self-contained and deterministic. See point 3
        // in the class doc comment.
        response.RequestMessage = _redirectedFinalUri.TryGetValue(path, out var finalUri)
            ? new HttpRequestMessage(HttpMethod.Get, finalUri)
            : request;

        return response;
    }

    public HttpClient Client() => new(this);
}
