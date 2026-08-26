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
        Assert.True(one.SignaturesComplete); // every listed signature was fetched - pinned, not incidental.
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

    // ---- FetchedAdvisory shape (Task 8 deviation: ContentHash added; round 3: SignaturesComplete added) ----

    [Fact]
    public void FetchedAdvisory_constructor_parameter_order_is_Id_ContentHash_PayloadBytes_ArmoredSignatures_SignaturesComplete()
    {
        // The compiler cannot catch an Id/ContentHash swap (both are `string`), so this pins the
        // exact positional order the deviation ruling (and, for the trailing bool, round 3's
        // append-only requirement) mandated rather than trusting call sites.
        var advisory = new FetchedAdvisory("id-1", "hash-1", [0x01, 0x02], ["sig-a", "sig-b"], false);

        Assert.Equal("id-1", advisory.Id);
        Assert.Equal("hash-1", advisory.ContentHash);
        Assert.Equal(new byte[] { 0x01, 0x02 }, advisory.PayloadBytes);
        Assert.Equal(["sig-a", "sig-b"], advisory.ArmoredSignatures);
        Assert.False(advisory.SignaturesComplete);
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
        Assert.False(one.SignaturesComplete); // zero because we could not look, not because there are none
    }

    [Fact]
    public async Task Missing_signature_index_leaves_signatures_incomplete()
    {
        var routes = Routes();
        routes.Remove($"{Feed}advisories/a1/signatures/index.json");
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched); // the advisory itself still comes through
        Assert.Empty(one.ArmoredSignatures);
        Assert.False(one.SignaturesComplete);
    }

    [Fact]
    public async Task Signature_index_of_literal_null_leaves_signatures_incomplete()
    {
        // Task 8 review round 4: JSON `null` deserializes to a C# null WITHOUT throwing, unlike
        // every other wrong shape - the pre-round-4 `?? []` coalesce silently turned that into an
        // empty array indistinguishable from a feed that legitimately listed zero signatures.
        var routes = Routes();
        routes[$"{Feed}advisories/a1/signatures/index.json"] = "null";
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched); // the advisory itself still comes through
        Assert.Empty(one.ArmoredSignatures);
        Assert.False(one.SignaturesComplete);
    }

    [Fact]
    public async Task Signature_index_of_the_wrong_json_shape_leaves_signatures_incomplete()
    {
        // Pins the parse-EXCEPTION branch specifically - distinct from the literal-null case
        // above, which does not throw. A syntactically valid JSON array of numbers throws when
        // deserialized as string[] (element type mismatch).
        var routes = Routes();
        routes[$"{Feed}advisories/a1/signatures/index.json"] = "[1,2]";
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        Assert.Empty(one.ArmoredSignatures);
        Assert.False(one.SignaturesComplete);
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
        Assert.False(one.SignaturesComplete); // one of the two listed signatures failed to fetch
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
        Assert.False(one.SignaturesComplete); // the 65th listed signature was never even attempted
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
        // Extension of round 3's SignaturesComplete contract to a case the coordinator's five
        // enumerated triggers didn't name explicitly but the same rationale covers: the index
        // LISTED this name, and no signature was ever fetched for it - "listed but unusable" is
        // just as much "not everything listed" as a fetch failure or a truncating cap would be.
        Assert.False(one.SignaturesComplete);
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
        Assert.False(one.SignaturesComplete); // same reasoning as the traversal case above
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
        // millisecond-scale per-request timeout so this test does not wait out the real 10-second
        // production default. The overall deadline is set generously (30s) so it is the
        // PER-REQUEST timeout that fires first, keeping this test focused on that one mechanism -
        // see Overall_poll_deadline_bounds_total_wall_time_even_with_many_hanging_requests for the
        // other one. CancellationToken.None is deliberate here - it proves the fetcher enforces
        // its OWN bound even when the caller supplies no cooperating token at all.
        var fetched = await fetcher.FetchAsync(
            Feed, new HashSet<string>(), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30), CancellationToken.None);
        sw.Stop();

        Assert.Empty(fetched);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Expected the per-request timeout to bound the hang; took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Overall_poll_deadline_bounds_total_wall_time_even_with_many_hanging_requests()
    {
        // Several advisories whose advisory.json all hang. Each is individually bounded by the
        // (generous, 10s production default) per-request timeout, but if the OVERALL deadline did
        // not exist, a poll could still take (per-request timeout) x (request count) to give up -
        // Task 8 review, Finding 3. The much shorter overall deadline here is what must actually
        // bound this test's wall time, not the per-request one.
        var routes = new Dictionary<string, string>();
        var indexEntries = new List<string>();
        var hangingPaths = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var id = $"a{i}";
            indexEntries.Add($$"""{"id":"{{id}}","path":"advisories/{{id}}","contentHash":"h{{id}}"}""");
            hangingPaths.Add($"{Feed}advisories/{id}/advisory.json");
        }
        routes[$"{Feed}index.json"] = "[" + string.Join(",", indexEntries) + "]";

        var http = new FakeHttp(routes, hangingRoutes: hangingPaths);
        var fetcher = new AdvisoryFetcher(http.Client());

        var sw = Stopwatch.StartNew();
        var fetched = await fetcher.FetchAsync(
            Feed, new HashSet<string>(), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        sw.Stop();

        Assert.Empty(fetched);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Expected the overall poll deadline to bound total wall time; took {sw.ElapsedMilliseconds}ms.");
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

    // ---- Task 8 review Finding 1 (Critical): failures must not permanently starve later entries ----

    [Fact]
    public async Task Failing_entries_do_not_permanently_starve_a_genuine_advisory_behind_them()
    {
        // 50 entries whose path is an absolute URL to a different host - containment-rejected,
        // costing zero network requests, but each one WAS attempted - followed by one genuine,
        // fetchable advisory. Before the fix, incrementing an "attempts" counter for each of these
        // (before the containment check, before any download) burned all 50 cap slots on entries
        // that could never succeed, so the genuine advisory was never reached on ANY poll - it
        // fails again at the same index position every single time, since a rejected entry's
        // content hash never reaches `known`. Reproduces on the FIRST poll now.
        var routes = Routes(); // supplies the genuine "a1" advisory's routes
        var indexEntries = new List<string>();
        for (var i = 0; i < 50; i++)
            indexEntries.Add($$"""{"id":"evil{{i}}","path":"https://evil.example/{{i}}","contentHash":"hevil{{i}}"}""");
        indexEntries.Add("""{"id":"a1","path":"advisories/a1","contentHash":"h1"}""");
        routes[$"{Feed}index.json"] = "[" + string.Join(",", indexEntries) + "]";

        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var genuine = Assert.Single(fetched);
        Assert.Equal("a1", genuine.Id);
        Assert.Contains(http.Requested, r => r.Contains("advisories/a1/advisory.json"));
    }

    [Fact]
    public async Task Advisories_whose_directory_404s_do_not_permanently_starve_a_genuine_advisory_behind_them()
    {
        // The same failure mode the review calls "benign feed rot": 50 entries that resolve fine
        // (stay contained, so each costs one real request) but whose advisory.json is simply
        // missing - no attacker involved at all, just stale directories that were removed.
        var routes = Routes();
        var indexEntries = new List<string>();
        for (var i = 0; i < 50; i++)
            indexEntries.Add($$"""{"id":"stale{{i}}","path":"advisories/stale{{i}}","contentHash":"hstale{{i}}"}""");
        indexEntries.Add("""{"id":"a1","path":"advisories/a1","contentHash":"h1"}""");
        routes[$"{Feed}index.json"] = "[" + string.Join(",", indexEntries) + "]";
        // Deliberately no routes registered for advisories/staleN/advisory.json - each 404s.

        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var genuine = Assert.Single(fetched);
        Assert.Equal("a1", genuine.Id);
    }

    [Fact]
    public async Task Hostile_index_with_many_failing_entries_is_bounded_by_the_request_ceiling()
    {
        // Enough 404-ing-but-contained entries (each costs exactly one request) to exceed
        // MaxRequestsPerPoll (4096), followed by one genuine advisory placed after the ceiling.
        // Proves the SEPARATE request ceiling actually bites, closing the DoS the fix for Finding 1
        // would otherwise have reopened (uncapped failures cost nothing against the now-successes-
        // only MaxAdvisoriesPerPoll). The genuine advisory is deliberately unreachable THIS poll -
        // that is the ceiling working as designed, not a regression of Finding 1's fix, which only
        // promises a genuine advisory is not starved by a realistic (tens of entries) amount of
        // failures ahead of it.
        var routes = Routes();
        var indexEntries = new List<string>();
        const int hostileCount = 4200; // > MaxRequestsPerPoll (4096); each entry costs exactly 1 request
        for (var i = 0; i < hostileCount; i++)
            indexEntries.Add($$"""{"id":"stale{{i}}","path":"advisories/stale{{i}}","contentHash":"hstale{{i}}"}""");
        indexEntries.Add("""{"id":"a1","path":"advisories/a1","contentHash":"h1"}""");
        routes[$"{Feed}index.json"] = "[" + string.Join(",", indexEntries) + "]";

        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched); // "a1" sits past the request ceiling this poll
        // Exactly 4096, not "at most": the index.json fetch itself consumes one of the 4096 budget
        // units (it goes through the same GetBytesAsync gate as everything else), leaving 4095 for
        // entries - so the budget is exhausted after the index fetch plus the first 4095 entries.
        Assert.Equal(4096, http.Requested.Count);
        Assert.DoesNotContain(http.Requested, r => r.Contains("advisories/a1/"));
    }

    // ---- Task 8 review Finding 2 (Important, SSRF): a redirect must not bypass containment ----

    [Fact]
    public async Task Response_redirected_to_a_different_host_is_discarded()
    {
        var routes = Routes();
        var redirectMap = new Dictionary<string, string>
        {
            [$"{Feed}advisories/a1/advisory.json"] = "https://evil.example/stolen-response"
        };
        var http = new FakeHttp(routes, redirectedFinalUri: redirectMap);
        var fetcher = new AdvisoryFetcher(http.Client());

        // FakeHttp returns HTTP 200 with a perfectly valid body for this path - only
        // RequestMessage.RequestUri (what a real redirect-following handler would leave behind)
        // says it actually came from evil.example. Must be discarded regardless.
        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Response_redirected_to_a_different_path_on_the_same_host_is_still_accepted()
    {
        // Proves the Finding 2 fix is not over-broad: a same-host redirect (e.g. a path or
        // trailing-slash normalisation a real GitHub Pages deployment might do) is not an SSRF
        // concern - it is still the same trusted origin - so it must not be rejected.
        //
        // No separate route is registered for advisory-v2.json: FakeHttp's redirect simulation
        // serves the body already registered under the ORIGINALLY-requested path (advisory.json,
        // from Routes()) and only relabels RequestMessage.RequestUri to the "final" URL - it does
        // not re-dispatch to a route keyed by that final URL. A route here would never be read and
        // would misleadingly suggest otherwise (Task 8 review round 2 nit).
        var routes = Routes();
        var redirectMap = new Dictionary<string, string>
        {
            [$"{Feed}advisories/a1/advisory.json"] = $"{Feed}advisories/a1/advisory-v2.json"
        };
        var http = new FakeHttp(routes, redirectedFinalUri: redirectMap);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Single(fetched);
    }

    // ---- Task 8 review Minor 4: sibling-prefix defence must not depend on the CALLER's feed URL ----

    [Theory]
    [InlineData("https://feed.example/repo/")]
    [InlineData("https://feed.example/repo")]
    public async Task Sibling_directory_prefix_trick_is_rejected_regardless_of_trailing_slash_on_the_feed_url(string feedUrl)
    {
        // Guards the specific trap a naive string-prefix check falls into:
        // "/repo-evil".StartsWith("/repo") is true. The defence is a path-SEGMENT prefix (baseUri's
        // own path always ends in '/' by construction - see WithTrailingSlash), which is immune to
        // this regardless of whether the CALLER's feed URL itself included a trailing slash.
        var routes = new Dictionary<string, string>
        {
            ["https://feed.example/repo/index.json"] = """
            [{"id":"a1","path":"../repo-evil/x","contentHash":"h1"}]
            """
        };
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(feedUrl, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        Assert.DoesNotContain(http.Requested, r => r.Contains("advisory.json"));
    }

    // ---- Task 8 review Minor 5: percent-encoded separators must be rejected too ----

    [Theory]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("%2e%2e%2f%2e%2e%2fetc%2fpasswd")]
    [InlineData("..%5c..%5cetc%5cpasswd")]
    public async Task Advisory_path_containing_a_percent_encoded_separator_is_rejected(string maliciousPath)
    {
        var routes = Routes();
        routes[$"{Feed}index.json"] = $$"""[{"id":"a1","path":"{{maliciousPath}}","contentHash":"h1"}]""";
        var fetcher = new AdvisoryFetcher(new FakeHttp(routes).Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
    }

    // ---- Task 8 review Minor 6: a query or fragment must not silently retarget the fetch ----

    [Theory]
    [InlineData("a1?x=1")]
    [InlineData("a1#fragment")]
    public async Task Advisory_path_containing_a_query_or_fragment_is_rejected(string maliciousPath)
    {
        var routes = Routes();
        routes[$"{Feed}index.json"] = $$"""[{"id":"a1","path":"{{maliciousPath}}","contentHash":"h1"}]""";
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        // Specifically proves it is rejected outright, not silently retargeted to whatever sits at
        // the feed's own base-level advisory.json.
        Assert.DoesNotContain(http.Requested, r => r.Contains("advisory.json"));
    }

    // ---- Task 8 review Minor 7: known-content-hash comparison must be case-insensitive ----

    [Fact]
    public async Task Known_content_hash_comparison_is_case_insensitive()
    {
        var http = new FakeHttp(Routes()); // "a1" has contentHash "h1" (lowercase)
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string> { "H1" }, CancellationToken.None);

        Assert.Empty(fetched); // must be recognised as already-known despite the case difference
    }

    // ---- Task 8 review Minor 8: the honest Content-Length fast path must also be exercised ----

    [Fact]
    public async Task Oversized_advisory_body_with_an_honest_content_length_is_rejected_via_the_fast_path()
    {
        var routes = Routes();
        var oversizedPath = $"{Feed}advisories/a1/advisory.json";
        routes[oversizedPath] = new string('x', 300 * 1024); // over the 256 KB cap
        var http = new FakeHttp(routes, honestContentLengthRoutes: [oversizedPath]);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
    }

    // ---- Task 8 review round 2, Finding 9: only an https feed URL is accepted ----

    [Fact]
    public async Task Http_feed_url_returns_empty_rather_than_being_silently_unfetchable_forever()
    {
        // An http feed URL is refused outright rather than attempted: IsUnderBase's post-response
        // redirect check requires an EXACT scheme match, and the standard http -> https redirect a
        // real GitHub Pages host issues would otherwise make every poll silently fetch nothing,
        // forever, with no exception, log, or signal. This proves the explicit, immediate refusal
        // instead - not the redirect scenario itself, which would need a real socket to reproduce
        // and is exactly what this refusal makes unreachable in the first place.
        var httpFeed = "http://feed.example/";
        var routes = new Dictionary<string, string>
        {
            [$"{httpFeed}index.json"] = """
            [{"id":"a1","path":"advisories/a1","contentHash":"h1"}]
            """
        };
        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(httpFeed, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(fetched);
        // Refused before even the index fetch - not merely "ends up empty for some other reason".
        Assert.Empty(http.Requested);
    }

    // ---- Task 8 review round 3: SignaturesComplete must reflect truncation, not just emptiness ----

    [Fact]
    public async Task Overall_deadline_exhaustion_mid_signature_loop_leaves_signatures_incomplete()
    {
        // advisory.json and signatures/index.json both succeed fast (in-memory, no delay), so the
        // advisory itself is fetched before the deadline fires; only sig-one.asc hangs. The much
        // shorter overall deadline (200ms) must cut the signature loop off mid-way - the per-request
        // timeout is left at the generous 10s production default so it is not what fires here.
        var routes = Routes();
        var hangingPath = $"{Feed}advisories/a1/signatures/sig-one.asc";
        var http = new FakeHttp(routes, hangingRoutes: [hangingPath]);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(
            Feed, new HashSet<string>(), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        var one = Assert.Single(fetched); // advisory.json already succeeded before the deadline fired
        Assert.False(one.SignaturesComplete);
    }

    [Fact]
    public async Task Request_budget_exhaustion_mid_signature_loop_leaves_signatures_incomplete()
    {
        // 4092 filler entries (each costs exactly one request via a 404 on advisory.json) placed
        // before the target advisory "a1", chosen so the poll-wide request budget (4096 - see
        // MaxRequestsPerPoll, Task 8 review Finding 1) is exhausted EXACTLY after "a1"'s
        // advisory.json + signatures/index.json + first signature file (sig-one.asc) succeed:
        // 1 (index.json) + 4092 (filler) + 1 (advisory.json) + 1 (signatures/index.json) +
        // 1 (sig-one.asc) = 4096. The second listed signature (sig-two.asc) is then denied by that
        // same budget - proving exhaustion marks the advisory it interrupts as incomplete
        // regardless of WHERE in the poll (an earlier advisory's fan-out, or this one's own) the
        // budget actually ran out.
        var routes = Routes(); // supplies a1's advisory.json, signatures/index.json (2 names), and both signature files
        var indexEntries = new List<string>();
        const int fillerCount = 4092;
        for (var i = 0; i < fillerCount; i++)
            indexEntries.Add($$"""{"id":"filler{{i}}","path":"advisories/filler{{i}}","contentHash":"hfiller{{i}}"}""");
        indexEntries.Add("""{"id":"a1","path":"advisories/a1","contentHash":"h1"}""");
        routes[$"{Feed}index.json"] = "[" + string.Join(",", indexEntries) + "]";
        // No routes registered for advisories/fillerN/advisory.json - each 404s, costing 1 request.

        var http = new FakeHttp(routes);
        var fetcher = new AdvisoryFetcher(http.Client());

        var fetched = await fetcher.FetchAsync(Feed, new HashSet<string>(), CancellationToken.None);

        var one = Assert.Single(fetched);
        Assert.Equal("a1", one.Id);
        var sig = Assert.Single(one.ArmoredSignatures);
        Assert.Equal("SIG-ONE", sig); // sig-two.asc was never even attempted - budget exhausted first
        Assert.False(one.SignaturesComplete);
        Assert.Equal(4096, http.Requested.Count);
    }
}
