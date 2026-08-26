using System.Diagnostics;
using BTCPayServer.Plugins.SecSwitch.Services;
using Xunit;

namespace BTCPayServer.Plugins.SecSwitch.Tests;

public class AdvisoryFetcherTests
{
    const string Feed = "https://feed.example/";

    static Dictionary<string, string> Routes() => new()
    {
        [$"{Feed}index.json"] = """
        [{"id":"a1","path":"advisories/a1","contentHash":"h1"}]
        """,
        [$"{Feed}advisories/a1/advisory.json"] = """{"id":"a1"}""",
        [$"{Feed}advisories/a1/signatures/index.json"] = """["sig-one.asc","sig-two.asc"]""",
        [$"{Feed}advisories/a1/signatures/sig-one.asc"] = "SIG-ONE",
        [$"{Feed}advisories/a1/signatures/sig-two.asc"] = "SIG-TWO"
    };

    [Fact]
    public async Task Fetches_a_new_advisory_with_all_signatures()
    {
        var http = new FakeHttp(Routes());
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        Assert.Equal("a1", one.Id);
        Assert.Equal("h1", one.ContentHash);
        Assert.Equal(2, one.ArmoredSignatures.Count);
        Assert.Contains("SIG-ONE", one.ArmoredSignatures);
    }

    [Fact]
    public async Task Skips_advisories_whose_content_hash_is_already_known()
    {
        var http = new FakeHttp(Routes());
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string> { "h1" }, CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("advisory.json"));
    }

    [Fact]
    public async Task Missing_advisory_body_is_skipped_not_thrown()
    {
        var routes = Routes();
        routes.Remove($"{Feed}advisories/a1/advisory.json");
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Unreachable_feed_returns_empty_rather_than_throwing()
    {
        var fetcher = new AdvisoryFetcher(new FakeHttp([]).Client());
        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);
        Assert.Empty(fetched);
    }

    // ---- FetchedAdvisory shape (Task 8 deviation: ContentHash added) ----

    [Fact]
    public void FetchedAdvisory_constructor_parameter_order_is_Id_then_ContentHash_then_PayloadBytes_then_ArmoredSignatures()
    {
        // The compiler cannot catch an Id/ContentHash swap (both are `string`), so this pins the
        // exact positional order the deviation ruling mandated rather than trusting call sites.
        var advisory = new FetchedAdvisory("id-1", "hash-1", [0x01, 0x02], ["sig-a", "sig-b"]);

        Assert.Equal("id-1", advisory.Id);
        Assert.Equal("hash-1", advisory.ContentHash);
        Assert.Equal(new byte[] { 0x01, 0x02 }, advisory.PayloadBytes);
        Assert.Equal(["sig-a", "sig-b"], advisory.ArmoredSignatures);
    }

    // ---- Null/malformed input to FetchAsync itself must never throw ----

    [Fact]
    public async Task Null_feed_url_returns_empty_rather_than_throwing()
    {
        var fetcher = new AdvisoryFetcher(new FakeHttp([]).Client());
        var fetched = await fetcher.FetchAsync(null!, new HashSet<string>(), CancellationToken.None);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Blank_feed_url_returns_empty_rather_than_throwing()
    {
        var fetcher = new AdvisoryFetcher(new FakeHttp([]).Client());
        var fetched = await fetcher.FetchAsync("   ", new HashSet<string>(), CancellationToken.None);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Malformed_feed_url_returns_empty_rather_than_throwing()
    {
        var fetcher = new AdvisoryFetcher(new FakeHttp([]).Client());
        var fetched = await fetcher.FetchAsync("not a url", new HashSet<string>(), CancellationToken.None);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Null_known_hash_set_is_treated_as_empty_rather_than_throwing()
    {
        var http = new FakeHttp(Routes());
        var fetcher = new AdvisoryFetcher(http.Client());

        // A null known-set must not silently suppress every advisory - the safe direction for a
        // security kill-switch is "discover it" not "silently skip it".
        var fetched = await fetcher.FetchAsync(Feed, null!, CancellationToken.None);

        Assert.Single(fetched);
    }

    [Fact]
    public async Task Index_entry_that_is_a_json_null_is_skipped_without_throwing()
    {
        var routes = Routes();
        routes[$"{Feed}index.json"] = """
        [null, {"id":"a1","path":"advisories/a1","contentHash":"h1"}]
        """;
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        Assert.Equal("a1", one.Id);
    }

    [Theory]
    [InlineData("""[{"path":"advisories/a1","contentHash":"h1"}]""")]        // missing id
    [InlineData("""[{"id":"a1","contentHash":"h1"}]""")]                     // missing path
    [InlineData("""[{"id":"a1","path":"advisories/a1"}]""")]                 // missing contentHash
    public async Task Index_entry_missing_a_required_field_is_skipped_without_throwing(string indexJson)
    {
        var routes = Routes();
        routes[$"{Feed}index.json"] = indexJson;
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
    }

    // ---- Bounded downloads: size caps ----

    [Fact]
    public async Task Oversized_index_is_skipped_entirely()
    {
        var routes = Routes();
        // Still syntactically valid JSON - the rejection must be about size, not shape.
        var oversizedHash = new string('x', 2 * 1024 * 1024); // over the 1 MB index cap
        routes[$"{Feed}index.json"] = $$"""[{"id":"a1","path":"advisories/a1","contentHash":"{{oversizedHash}}"}]""";
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Oversized_advisory_body_is_skipped()
    {
        var routes = Routes();
        // Valid JSON, oversized only in a field's value - proves the rejection is the byte cap,
        // not a JSON-parse failure (AdvisoryFetcher never parses advisory.json anyway, but this
        // keeps the test meaningful even if that changes later).
        var padding = new string('x', 260 * 1024); // over the 256 KB advisory cap
        routes[$"{Feed}advisories/a1/advisory.json"] = $$"""{"id":"a1","description":"{{padding}}"}""";
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Oversized_signature_index_is_skipped_leaving_no_signatures()
    {
        var routes = Routes();
        // One long-but-valid JSON string, over the 16 KB signatures-index cap.
        routes[$"{Feed}advisories/a1/signatures/index.json"] = $$"""["{{new string('x', 20 * 1024)}}"]""";
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched); // the advisory itself still comes through
        Assert.Empty(one.ArmoredSignatures); // but with none of its signatures
    }

    [Fact]
    public async Task Oversized_signature_file_is_skipped_but_sibling_signature_still_fetched()
    {
        var routes = Routes();
        routes[$"{Feed}advisories/a1/signatures/sig-one.asc"] = new string('x', 70 * 1024); // over the 64 KB cap
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        var sig = Assert.Single(one.ArmoredSignatures); // sig-one dropped, sig-two survives
        Assert.Equal("SIG-TWO", sig);
    }

    // ---- Bounded downloads: count caps ----

    [Fact]
    public async Task Advisories_beyond_the_per_poll_cap_are_deferred_not_dropped()
    {
        // 51 distinct advisories: one more than AdvisoryFetcher's internal 50-per-poll cap.
        var routes = new Dictionary<string, string>();
        var indexEntries = new List<string>();
        for (var i = 0; i < 51; i++)
        {
            var id = $"a{i}";
            indexEntries.Add($$"""{"id":"{{id}}","path":"advisories/{{id}}","contentHash":"h{{id}}"}""");
            routes[$"{Feed}advisories/{id}/advisory.json"] = $$"""{"id":"{{id}}"}""";
            routes[$"{Feed}advisories/{id}/signatures/index.json"] = "[]";
        }
        routes[$"{Feed}index.json"] = "[" + string.Join(",", indexEntries) + "]";

        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());
        var known = new HashSet<string>();

        var firstPoll = await fetcher.FetchAsync(Feed, known, CancellationToken.None);
        Assert.Equal(50, firstPoll.Count); // capped, not all 51

        foreach (var a in firstPoll)
            known.Add(a.ContentHash);

        var secondPoll = await fetcher.FetchAsync(Feed, known, CancellationToken.None);
        var leftover = Assert.Single(secondPoll); // the 51st was deferred, not dropped forever
        Assert.DoesNotContain(leftover.ContentHash, known);
    }

    [Fact]
    public async Task Signature_files_beyond_the_per_advisory_cap_are_ignored()
    {
        var routes = Routes();
        var names = new List<string>();
        for (var i = 0; i < 65; i++) // one more than the 64-per-advisory cap
        {
            var name = $"sig-{i}.asc";
            names.Add($"\"{name}\"");
            routes[$"{Feed}advisories/a1/signatures/{name}"] = $"SIG-{i}";
        }
        routes[$"{Feed}advisories/a1/signatures/index.json"] = "[" + string.Join(",", names) + "]";

        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());
        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        Assert.Equal(64, one.ArmoredSignatures.Count);
    }

    // ---- Path traversal: entry.Path and signature file names are attacker-influenced ----

    [Fact]
    public async Task Advisory_path_climbing_out_of_the_feed_subdirectory_is_skipped()
    {
        // Needs real sub-path segments to climb out of - "https://feed.example/" alone has an
        // AbsolutePath of just "/", so there is no narrower directory for ".." to escape.
        const string nestedFeed = "https://feed.example/feeds/v1/";
        var routes = new Dictionary<string, string>
        {
            [$"{nestedFeed}index.json"] = """
            [{"id":"a1","path":"../../../etc/passwd","contentHash":"h1"}]
            """
        };
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(nestedFeed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("advisory.json"));
    }

    [Fact]
    public async Task Advisory_path_as_an_absolute_url_to_another_host_is_skipped()
    {
        var routes = Routes();
        routes[$"{Feed}index.json"] = """
        [{"id":"a1","path":"https://evil.example/pwn","contentHash":"h1"}]
        """;
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("evil.example"));
    }

    [Fact]
    public async Task Advisory_path_as_a_protocol_relative_reference_is_skipped()
    {
        var routes = Routes();
        routes[$"{Feed}index.json"] = """
        [{"id":"a1","path":"//evil.example/pwn","contentHash":"h1"}]
        """;
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("evil.example"));
    }

    [Fact]
    public async Task Advisory_path_using_userinfo_to_disguise_a_different_host_is_skipped()
    {
        // A naive `composed.StartsWith(feedBaseUrl)` check would be fooled: this string literally
        // starts with "https://feed.example" - but the URI's actual host is evil.example, with
        // "feed.example" reduced to meaningless userinfo (the "user@" part of "user@host").
        var routes = Routes();
        routes[$"{Feed}index.json"] = """
        [{"id":"a1","path":"https://feed.example@evil.example/pwn","contentHash":"h1"}]
        """;
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("evil.example"));
    }

    [Fact]
    public async Task Advisory_path_using_backslashes_is_still_contained()
    {
        // .NET's Uri class normalizes backslashes to forward slashes for hierarchical URIs before
        // resolving dot-segments, so a Windows-style "..\" spelling must be caught exactly like
        // "../" is - empirically confirmed here rather than assumed.
        const string nestedFeed = "https://feed.example/feeds/v1/";
        var routes = new Dictionary<string, string>
        {
            [$"{nestedFeed}index.json"] = """
            [{"id":"a1","path":"..\\..\\..\\etc\\passwd","contentHash":"h1"}]
            """
        };
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(nestedFeed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("advisory.json"));
    }

    [Fact]
    public async Task Signature_name_climbing_out_of_its_signatures_directory_is_skipped_but_others_still_fetched()
    {
        var routes = Routes();
        routes[$"{Feed}advisories/a1/signatures/index.json"] = """
        ["../../../evil.asc","sig-two.asc"]
        """;
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        var sig = Assert.Single(one.ArmoredSignatures);
        Assert.Equal("SIG-TWO", sig);
        Assert.DoesNotContain(http.Requested, r => r.Contains("evil.asc"));
    }

    [Fact]
    public async Task Signature_name_as_an_absolute_url_to_another_host_is_skipped()
    {
        var routes = Routes();
        routes[$"{Feed}advisories/a1/signatures/index.json"] = """
        ["https://evil.example/evil.asc","sig-two.asc"]
        """;
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        var sig = Assert.Single(one.ArmoredSignatures);
        Assert.Equal("SIG-TWO", sig);
        Assert.DoesNotContain(http.Requested, r => r.Contains("evil.example"));
    }

    // ---- Per-request timeout: a hanging response must not stall the poll ----

    [Fact]
    public async Task Hanging_response_times_out_and_is_skipped_rather_than_stalling_the_poll()
    {
        var routes = Routes();
        var hangingPath = $"{Feed}advisories/a1/advisory.json";
        var http = new FakeHttp(routes, hangingRoutes: [hangingPath]);
        var fetcher = new AdvisoryFetcher(http.Client());

        var sw = Stopwatch.StartNew();
        // Internal overload (test-only seam - see AdvisoryFetcher's InternalsVisibleTo): a
        // millisecond-scale timeout so this test does not wait out the real 10-second production
        // default. CancellationToken.None is deliberate here - it proves the fetcher enforces its
        // OWN bound even when the caller supplies no cooperating token at all.
        var fetched = await fetcher.FetchAsync(
            Feed, new HashSet<string>(), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        sw.Stop();

        Assert.Empty(fetched);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Expected the per-request timeout to bound the hang; took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Caller_supplied_cancellation_is_honoured_promptly()
    {
        var routes = Routes();
        var hangingPath = $"{Feed}advisories/a1/advisory.json";
        var http = new FakeHttp(routes, hangingRoutes: [hangingPath]);
        var fetcher = new AdvisoryFetcher(http.Client());

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var sw = Stopwatch.StartNew();
        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), cts.Token);
        sw.Stop();

        Assert.Empty(fetched);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Expected the caller's token to end the wait promptly; took {sw.ElapsedMilliseconds}ms.");
    }
}
