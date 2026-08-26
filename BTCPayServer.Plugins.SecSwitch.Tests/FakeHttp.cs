using System.Net;
using System.Text;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

/// <summary>
/// A fake HTTP feed for AdvisoryFetcherTests. Two deliberate deviations from a fake that just
/// echoes the requested route, both existing to exercise AdvisoryFetcher's hardening rather than
/// only its happy path:
///
/// 1. Every response's Content-Length header is stripped after construction, unconditionally.
///    AdvisoryFetcher's byte-size caps are enforced two ways: a Content-Length fast path that
///    rejects an honestly-oversized response before reading any of its body, and a running-total
///    check while streaming that catches a response which lies about or omits Content-Length.
///    Stripping the header here forces every test in this file - not just a specially crafted one
///    - to exercise the streaming check, which is the one that has to hold against a genuinely
///    hostile server (a real one has no obligation to send an honest, or any, Content-Length).
/// 2. <paramref name="hangingRoutes"/> lets a test simulate a slow-loris response that never
///    completes on its own: SendAsync awaits indefinitely on the request's own CancellationToken
///    for those paths, so the request only ever ends when that token is cancelled - by
///    AdvisoryFetcher's own per-request timeout, in the test that uses this.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    readonly Dictionary<string, string> _routes;
    readonly HashSet<string> _hangingRoutes;
    public List<string> Requested { get; } = [];

    public FakeHttp(Dictionary<string, string> routes, IEnumerable<string>? hangingRoutes = null)
    {
        _routes = routes;
        _hangingRoutes = new HashSet<string>(hangingRoutes ?? []);
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
        response.Content.Headers.ContentLength = null;
        return response;
    }

    public HttpClient Client() => new(this);
}
