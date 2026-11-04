using System.Net;
using System.Text;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryFeedTests
{
    const string Url = "https://example.test/advisories.json";
    const string Body =
        """{"advisories":[{"id":"a1","identifier":"Plug","affectedVersions":"<2.0.0","severity":"high"},{"id":"bad"}]}""";

    sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond());
        }
    }

    static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    sealed class HangingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    [Fact]
    public async Task A_body_that_never_finishes_times_out_instead_of_stalling_the_poll()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new HangingContent() });

        var error = await Assert.ThrowsAsync<TimeoutException>(() => AdvisoryFeed.FetchAsync(
            new HttpClient(handler), Url, CancellationToken.None, TimeSpan.FromMilliseconds(200)));

        Assert.Contains("did not answer", error.Message);
    }

    sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Shutdown_cancellation_is_not_reported_as_a_timeout()
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AdvisoryFeed.FetchAsync(new HttpClient(new HangingHandler()), Url, shutdown.Token));
    }

    [Fact]
    public async Task Fetches_and_parses_the_feed_and_reports_skipped_entries()
    {
        var handler = new StubHandler(() => Ok(Body));

        var result = await AdvisoryFeed.FetchAsync(new HttpClient(handler), Url, CancellationToken.None);

        Assert.Equal("a1", Assert.Single(result.Advisories).Id);
        Assert.Single(result.Skipped);
        Assert.Equal(new Uri(Url), Assert.Single(handler.Requested));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task A_non_success_status_fails_the_fetch_including_redirects(HttpStatusCode status)
    {
        var handler = new StubHandler(() => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => AdvisoryFeed.FetchAsync(new HttpClient(handler), Url, CancellationToken.None));
    }

    [Fact]
    public async Task A_body_over_the_size_cap_fails_the_fetch()
    {
        var handler = new StubHandler(() => Ok(new string(' ', AdvisoryFeed.MaxFeedBytes + 1)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => AdvisoryFeed.FetchAsync(new HttpClient(handler), Url, CancellationToken.None));
    }

    [Fact]
    public async Task A_malformed_body_fails_the_fetch()
    {
        var handler = new StubHandler(() => Ok("<html>not a feed</html>"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => AdvisoryFeed.FetchAsync(new HttpClient(handler), Url, CancellationToken.None));
    }

    [Fact]
    public async Task A_non_https_url_is_refused_before_any_request()
    {
        var handler = new StubHandler(() => Ok(Body));

        await Assert.ThrowsAsync<ArgumentException>(
            () => AdvisoryFeed.FetchAsync(new HttpClient(handler), "http://example.test/advisories.json", CancellationToken.None));
        Assert.Empty(handler.Requested);
    }

    [Theory]
    [InlineData("https://example.test/a.json", true)]
    [InlineData("HTTPS://EXAMPLE.TEST/a.json", true)]
    [InlineData("http://example.test/a.json", false)]
    [InlineData("ftp://example.test/a.json", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative/a.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedFeedUrl_accepts_only_absolute_https(string? url, bool expected)
    {
        Assert.Equal(expected, AdvisoryFeed.IsSupportedFeedUrl(url));
    }
}
