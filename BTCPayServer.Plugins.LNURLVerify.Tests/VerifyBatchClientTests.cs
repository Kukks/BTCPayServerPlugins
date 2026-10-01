using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.LNURLVerify;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class VerifyBatchClientTests
{
    const string Endpoint = "https://vb.example/lnurl/verifyBatch";
    const string A = "https://vb.example/lnurl/verify/a";
    const string B = "https://vb.example/lnurl/verify/b";

    static Task<BatchOutcome> Fetch(FakeHttp http, params string[] urls) =>
        VerifyBatchClient.Fetch(http.Client(), Endpoint, urls, TestContext.Current.CancellationToken);

    [Fact]
    public void BuildUri_appends_each_verify_url_escaped_after_an_existing_query()
    {
        var uri = VerifyBatchClient.BuildUri(Endpoint + "?key=abc", new[] { A + "?x=1&y=2", B });

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("abc", query["key"]);
        Assert.Equal(new[] { A + "?x=1&y=2", B }, query.GetValues("verify"));
    }

    [Fact]
    public async Task Ok_keys_each_item_by_the_exact_url_and_asks_for_json_not_a_stream()
    {
        var http = new FakeHttp().When(_ => true, _ => (HttpStatusCode.OK,
            "{\"status\":\"OK\",\"results\":{\"" + A + "\":{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"x\"}," +
            "\"" + B + "\":{\"status\":\"ERROR\",\"reason\":\"unknown verify url\"}}}"));

        var outcome = await Fetch(http, A, B);

        Assert.Equal(BatchOutcomeKind.Ok, outcome.Kind);
        Assert.False(outcome.Results![A]["settled"]!.Value<bool>());
        Assert.Equal("ERROR", outcome.Results[B]["status"]!.Value<string>());
        Assert.Contains("application/json", http.AcceptHeaders.Single());
        Assert.DoesNotContain("text/event-stream", http.AcceptHeaders.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestUriTooLong, BatchOutcomeKind.TooLong)]
    [InlineData(HttpStatusCode.RequestHeaderFieldsTooLarge, BatchOutcomeKind.TooLong)]
    [InlineData(HttpStatusCode.NotFound, BatchOutcomeKind.Unsupported)]
    [InlineData(HttpStatusCode.MethodNotAllowed, BatchOutcomeKind.Unsupported)]
    [InlineData(HttpStatusCode.NotImplemented, BatchOutcomeKind.Unsupported)]
    [InlineData(HttpStatusCode.TooManyRequests, BatchOutcomeKind.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, BatchOutcomeKind.Failed)]
    public async Task Status_codes_are_classified(HttpStatusCode code, BatchOutcomeKind expected)
    {
        var http = new FakeHttp().When(_ => true, _ => (code, "{}"));

        Assert.Equal(expected, (await Fetch(http, A)).Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"status\":\"ERROR\",\"reason\":\"verify parameter required\"}")]
    [InlineData("{\"status\":\"OK\"}")]
    public async Task Unusable_bodies_fail(string body)
    {
        var http = new FakeHttp().When(_ => true, _ => (HttpStatusCode.OK, body));

        Assert.Equal(BatchOutcomeKind.Failed, (await Fetch(http, A)).Kind);
    }

    [Fact]
    public async Task A_response_echoing_none_of_the_urls_is_unsupported()
    {
        // A server that canonicalises keys (here: a trailing slash) instead of echoing them byte for byte.
        var http = new FakeHttp().When(_ => true, _ => (HttpStatusCode.OK,
            "{\"status\":\"OK\",\"results\":{\"" + A + "/\":{\"status\":\"OK\",\"settled\":true}}}"));

        Assert.Equal(BatchOutcomeKind.Unsupported, (await Fetch(http, A)).Kind);
    }

    [Fact]
    public async Task A_transport_failure_fails()
    {
        var http = new FakeHttp().When(_ => true, _ => throw new HttpRequestException("boom"));

        Assert.Equal(BatchOutcomeKind.Failed, (await Fetch(http, A)).Kind);
    }
}
