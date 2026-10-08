using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

[Collection(RegistryCollection.Name)]
public class LNURLVerifyPollerTests
{
    const string SpecBolt11 =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";
    const string Pending = "{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"x\"}";
    static string Settled(string preimage) => "{\"status\":\"OK\",\"settled\":true,\"preimage\":\"" + preimage + "\",\"pr\":\"x\"}";
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    static string NewHash() => Hex(RandomNumberGenerator.GetBytes(32));
    static string NewHost(string prefix) => prefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".example";

    static TrackedInvoice Batched(string host, string hash, DateTimeOffset? expiresAt = null) =>
        new(hash, SpecBolt11, $"https://{host}/lnurl/verify/{hash}", host, $"https://{host}/pay",
            expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), $"https://{host}/lnurl/verifyBatch");

    static LNURLVerifyPollerService NewPoller(FakeHttp http, int intervalMs) =>
        new(NullLogger<LNURLVerifyPollerService>.Instance, new FakeHttpClientFactory(http), TimeSpan.FromMilliseconds(intervalMs));

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "condition not reached within 5s");
    }

    static async Task Polling(LNURLVerifyPollerService poller, IEnumerable<string> hashes, Func<Task> body)
    {
        var ct = TestContext.Current.CancellationToken;
        await poller.StartAsync(ct);
        try { await body(); }
        finally
        {
            await poller.StopAsync(ct);
            foreach (var h in hashes) TrackedInvoiceRegistry.Remove(h);
        }
    }

    static (FakeHttp Http, FakeBatchServer Server, string[] Hashes) Endpoint(string host, int invoices,
        Func<string[], HttpStatusCode> status, Func<string, string?> item)
    {
        var hashes = Enumerable.Range(0, invoices).Select(_ => NewHash()).ToArray();
        foreach (var h in hashes) TrackedInvoiceRegistry.Add(Batched(host, h));
        var http = new FakeHttp();
        return (http, new FakeBatchServer(http, host, status, item), hashes);
    }

    static TrackedDestination Destination(string host, bool batched = true, DateTimeOffset? expiresAt = null)
    {
        var id = Hex(RandomNumberGenerator.GetBytes(16));
        return new TrackedDestination("inv-" + id, "LNURL-ARKADE", "tark1q" + id, $"https://{host}/lnurl/verify/{id}",
            batched ? $"https://{host}/lnurl/verifyBatch" : null, 5_000_000, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1));
    }

    static string DestinationState(bool settled, string? reference = null) =>
        new JObject { ["status"] = "OK", ["settled"] = settled, ["paymentOption"] = "arkade", ["paymentReference"] = reference }.ToString();

    static async Task Tracking(TrackedDestination[] destinations, Func<Task> body)
    {
        foreach (var d in destinations) TrackedDestinationRegistry.Add(d);
        try { await body(); }
        finally { foreach (var d in destinations) TrackedDestinationRegistry.Remove(d.VerifyUrl); }
    }

    static async Task<string> Observe(Action<Action<TrackedDestination, string>> subscribe,
        Action<Action<TrackedDestination, string>> unsubscribe, TrackedDestination d, Func<Task> run)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<TrackedDestination, string> handler = (x, value) => { if (x == d) seen.TrySetResult(value); };
        subscribe(handler);
        try
        {
            var running = run();
            var value = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await running;
            return value;
        }
        finally { unsubscribe(handler); }
    }

    [Fact]
    public async Task One_request_per_cycle_carries_every_pending_invoice_of_an_endpoint()
    {
        var host = NewHost("one");
        var (http, server, hashes) = Endpoint(host, 5, _ => HttpStatusCode.OK, _ => Pending);
        var expected = hashes.Select(h => $"https://{host}/lnurl/verify/{h}").OrderBy(u => u, StringComparer.Ordinal).ToArray();

        await Polling(NewPoller(http, 30), hashes, async () =>
        {
            await Until(() => server.Batches.Count >= 3);
            Assert.All(server.Batches, b => Assert.Equal(expected, b.OrderBy(u => u, StringComparer.Ordinal)));
            Assert.Empty(server.Singles);
            Assert.All(hashes, h =>
                Assert.True(TrackedInvoiceRegistry.TryGetResult(h, out var r) && r!.Status == LightningInvoiceStatus.Unpaid));
        });
    }

    [Fact]
    public async Task More_than_250_pending_invoices_split_into_chunks_of_250()
    {
        var (http, server, hashes) = Endpoint(NewHost("chunk"), 300, _ => HttpStatusCode.OK, _ => Pending);

        await Polling(NewPoller(http, 10_000), hashes, async () =>
        {
            await Until(() => server.Batches.Count >= 2);
            Assert.Equal(new[] { 250, 50 }, server.Batches.Take(2).Select(b => b.Length));
        });
    }

    [Fact]
    public async Task Request_too_long_halves_the_chunk_and_remembers_it()
    {
        var (http, server, hashes) = Endpoint(NewHost("long"), 5,
            urls => urls.Length > 2 ? HttpStatusCode.RequestUriTooLong : HttpStatusCode.OK, _ => Pending);

        await Polling(NewPoller(http, 30), hashes, async () =>
        {
            await Until(() => server.Batches.Count >= 7);
            Assert.Equal(new[] { 5, 2, 2, 1, 2, 2, 1 }, server.Batches.Take(7).Select(b => b.Length));
        });
    }

    [Fact]
    public async Task Request_too_long_even_for_one_url_falls_back_instead_of_looping()
    {
        var (http, server, hashes) = Endpoint(NewHost("long1"), 3, _ => HttpStatusCode.RequestUriTooLong, _ => Pending);

        await Polling(NewPoller(http, 20), hashes, async () =>
        {
            await Until(() => server.Singles.Count >= 3);
            Assert.Equal(new[] { 3, 1, 1, 1 }, server.Batches.Select(b => b.Length));
        });
    }

    [Fact]
    public async Task An_endpoint_that_keeps_failing_falls_back_to_per_invoice_polling()
    {
        var (http, server, hashes) = Endpoint(NewHost("broken"), 2, _ => HttpStatusCode.InternalServerError, _ => Pending);

        await Polling(NewPoller(http, 20), hashes, async () =>
        {
            await Until(() => server.Singles.Count >= 2);
            Assert.Equal(3, server.Batches.Count);
        });
    }

    [Fact]
    public async Task An_endpoint_without_verifyBatch_falls_back_to_per_invoice_polling()
    {
        var (http, server, hashes) = Endpoint(NewHost("nobatch"), 2, _ => HttpStatusCode.NotFound, _ => Pending);

        await Polling(NewPoller(http, 30), hashes, async () =>
        {
            await Until(() => server.Singles.Count >= 4);
            Assert.Single(server.Batches);
        });
    }

    [Fact]
    public async Task A_throttled_endpoint_backs_off_without_falling_back_to_per_invoice()
    {
        var (http, server, hashes) = Endpoint(NewHost("throttled"), 2, _ => HttpStatusCode.TooManyRequests, _ => Pending);

        await Polling(NewPoller(http, 20), hashes, async () =>
        {
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Empty(server.Singles);
            Assert.InRange(server.Batches.Count, 1, 6);
        });
    }

    [Fact]
    public async Task Per_item_errors_mean_not_found()
    {
        var host = NewHost("items");
        var (known, unknown) = (NewHash(), NewHash());
        foreach (var h in new[] { known, unknown }) TrackedInvoiceRegistry.Add(Batched(host, h));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, url =>
            url.EndsWith(known) ? Pending : "{\"status\":\"ERROR\",\"reason\":\"unknown verify url\"}");

        await Polling(NewPoller(http, 30), new[] { known, unknown }, async () =>
        {
            await Until(() => TrackedInvoiceRegistry.TryGetResult(unknown, out _) && TrackedInvoiceRegistry.TryGetResult(known, out _));
            Assert.True(TrackedInvoiceRegistry.TryGetResult(unknown, out var notFound));
            Assert.Null(notFound);
            Assert.True(TrackedInvoiceRegistry.TryGetResult(known, out var pending));
            Assert.Equal(LightningInvoiceStatus.Unpaid, pending!.Status);
            Assert.Empty(server.Singles);
        });
    }

    [Fact]
    public async Task A_url_missing_from_a_batch_answer_is_polled_on_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = NewHost("partial");
        var preimage = RandomNumberGenerator.GetBytes(32);
        var (answered, missing) = (NewHash(), Hex(SHA256.HashData(preimage)));
        TrackedInvoiceRegistry.Add(Batched(host, answered));
        TrackedInvoiceRegistry.Add(Batched(host, missing));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK,
            url => url.EndsWith(answered) ? Pending : null, single: _ => Settled(Hex(preimage)));
        using var listener = new LNURLVerifyListener(t => t.PaymentHash == missing);
        var waiter = listener.WaitInvoice(ct);

        await Polling(NewPoller(http, 30), new[] { answered, missing }, async () =>
        {
            Assert.Equal(missing, (await waiter.WaitAsync(TimeSpan.FromSeconds(5), ct)).PaymentHash);
            Assert.All(server.Singles, u => Assert.EndsWith(missing, u));
        });
    }

    [Fact]
    public async Task Settled_batch_items_publish_only_with_a_valid_preimage()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = NewHost("settle");
        var preimage = RandomNumberGenerator.GetBytes(32);
        var good = Hex(SHA256.HashData(preimage));
        var bad = NewHash();
        TrackedInvoiceRegistry.Add(Batched(host, good));
        TrackedInvoiceRegistry.Add(Batched(host, bad));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, url =>
            url.EndsWith(good) ? Settled(Hex(preimage)) : url.EndsWith(bad) ? Settled(new string('1', 64)) : null);
        using var listener = new LNURLVerifyListener(t => t.PaymentHash == good || t.PaymentHash == bad);
        var waiter = listener.WaitInvoice(ct);

        await Polling(NewPoller(http, 30), new[] { good, bad }, async () =>
        {
            var paid = await waiter.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(good, paid.PaymentHash);
            Assert.Equal(Hex(preimage), paid.Preimage);
            await Until(() => TrackedInvoiceRegistry.TryGetResult(bad, out _));
            Assert.True(TrackedInvoiceRegistry.TryGetResult(bad, out var unproven));
            Assert.Equal(LightningInvoiceStatus.Unpaid, unproven!.Status);
            Assert.True(TrackedInvoiceRegistry.TryGet(bad, out _));
            Assert.NotEmpty(server.Batches);
            Assert.Empty(server.Singles);
        });
    }

    [Fact]
    public async Task Expired_invoices_are_dropped_before_any_batch_request()
    {
        var host = NewHost("expired");
        var (live, expired) = (NewHash(), NewHash());
        TrackedInvoiceRegistry.Add(Batched(host, live));
        TrackedInvoiceRegistry.Add(Batched(host, expired, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => Pending);

        await Polling(NewPoller(http, 30), new[] { live, expired }, async () =>
        {
            await Until(() => server.Batches.Count >= 2);
            Assert.All(server.Batches, b => Assert.DoesNotContain(b, u => u.EndsWith(expired)));
            Assert.False(TrackedInvoiceRegistry.TryGet(expired, out _));
        });
    }

    [Fact]
    public async Task A_failing_endpoint_does_not_hold_up_another()
    {
        var ct = TestContext.Current.CancellationToken;
        var (down, up) = (NewHost("down"), NewHost("up"));
        var stuck = NewHash();
        var preimage = RandomNumberGenerator.GetBytes(32);
        var paidHash = Hex(SHA256.HashData(preimage));
        TrackedInvoiceRegistry.Add(Batched(down, stuck));
        TrackedInvoiceRegistry.Add(Batched(up, paidHash));
        var http = new FakeHttp();
        var failing = new FakeBatchServer(http, down, _ => HttpStatusCode.InternalServerError, _ => Pending);
        new FakeBatchServer(http, up, _ => HttpStatusCode.OK, url => url.EndsWith(paidHash) ? Settled(Hex(preimage)) : null);
        using var listener = new LNURLVerifyListener(t => t.PaymentHash == paidHash);
        var waiter = listener.WaitInvoice(ct);

        await Polling(NewPoller(http, 30), new[] { stuck, paidHash }, async () =>
        {
            Assert.Equal(paidHash, (await waiter.WaitAsync(TimeSpan.FromSeconds(5), ct)).PaymentHash);
            Assert.True(TrackedInvoiceRegistry.TryGet(stuck, out _));
            Assert.Empty(failing.Singles);
        });
    }

    [Fact]
    public async Task Poller_publishes_settled_and_prunes()
    {
        var host = "poll.example";
        var hash = new string('c', 64);
        var t = new TrackedInvoice(hash, "lnbcrt1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
            DateTimeOffset.UtcNow.AddHours(1));
        TrackedInvoiceRegistry.Add(t);

        // Return Paid only for THIS invoice, null for any other (so concurrent test-class invoices
        // in the shared static registry are left untouched).
        LNURLVerifyPollerService.PollOverride = (ti, _) => Task.FromResult<LightningInvoice?>(
            ti.PaymentHash == hash
                ? new LightningInvoice { Id = ti.PaymentHash, PaymentHash = ti.PaymentHash, Status = LightningInvoiceStatus.Paid }
                : null);

        using var listener = new LNURLVerifyListener(ti => ti.PaymentHash == hash);
        var waiter = listener.WaitInvoice(TestContext.Current.CancellationToken);

        var poller = new LNURLVerifyPollerService(
            NullLogger<LNURLVerifyPollerService>.Instance, new SimpleHttpClientFactory(), TimeSpan.FromMilliseconds(20));
        await poller.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var seen = await waiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(hash, seen.PaymentHash);
            Assert.False(TrackedInvoiceRegistry.TryGet(hash, out _));
        }
        finally
        {
            await poller.StopAsync(TestContext.Current.CancellationToken);
            LNURLVerifyPollerService.PollOverride = null;
        }
    }

    [Fact]
    public async Task Poller_settles_many_invoices_concurrently_without_races()
    {
        // 60 invoices across 4 hosts. PollOverride settles even-indexed and throws on odd-indexed, so the
        // concurrent success (MarkSettled) and error (backoff) paths run together under the concurrency
        // gate — the exact mix a non-thread-safe _backoff would corrupt.
        var hashes = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            var hash = $"conc{i:D2}".PadRight(64, '0');
            hashes.Add(hash);
            var host = $"conc{i % 4}.example";
            TrackedInvoiceRegistry.Add(new TrackedInvoice(
                hash, "lnbcrt1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
                DateTimeOffset.UtcNow.AddHours(1)));
        }

        LNURLVerifyPollerService.PollOverride = (t, _) =>
        {
            var idx = int.Parse(t.PaymentHash.Substring(4, 2));
            if (idx % 2 == 0)
                return Task.FromResult<LightningInvoice?>(new LightningInvoice
                { Id = t.PaymentHash, PaymentHash = t.PaymentHash, Status = LightningInvoiceStatus.Paid });
            throw new Exception("simulated poll failure");
        };

        var settled = 0;
        void Handler(TrackedInvoice t, LightningInvoice inv) => Interlocked.Increment(ref settled);
        TrackedInvoiceRegistry.Settled += Handler;

        var poller = new LNURLVerifyPollerService(
            NullLogger<LNURLVerifyPollerService>.Instance, new SimpleHttpClientFactory(), TimeSpan.FromMilliseconds(10));
        await poller.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            for (int i = 0; i < 300 && Volatile.Read(ref settled) < 30; i++)
                await Task.Delay(25, TestContext.Current.CancellationToken);

            Assert.Equal(30, Volatile.Read(ref settled));                 // all 30 even invoices settled once
            foreach (var h in hashes)
                if (int.Parse(h.Substring(4, 2)) % 2 == 0)
                    Assert.True(TrackedInvoiceRegistry.TryGetSettled(h, out _)); // retrievable as Paid
        }
        finally
        {
            await poller.StopAsync(TestContext.Current.CancellationToken);
            LNURLVerifyPollerService.PollOverride = null;
            TrackedInvoiceRegistry.Settled -= Handler;
            foreach (var h in hashes) TrackedInvoiceRegistry.Remove(h);
            // NOTE: don't PruneSettled() here — a global prune would wipe other (parallel) test classes'
            // settled entries. This test's settled hashes are unique (conc*) and expire via their grace.
        }
    }

    [Fact]
    public async Task Poller_records_unpaid_results_for_GetInvoice()
    {
        var ct = TestContext.Current.CancellationToken;
        var hash = "recd".PadRight(64, '0');
        TrackedInvoiceRegistry.Add(new TrackedInvoice(hash, "lnbcrt1", $"https://recd.example/verify/{hash}", "recd.example",
            "https://recd.example/pay", DateTimeOffset.UtcNow.AddHours(1)));
        LNURLVerifyPollerService.PollOverride = (t, _) => Task.FromResult<LightningInvoice?>(t.PaymentHash == hash
            ? new LightningInvoice { Id = hash, PaymentHash = hash, Status = LightningInvoiceStatus.Unpaid }
            : null);
        var poller = new LNURLVerifyPollerService(
            NullLogger<LNURLVerifyPollerService>.Instance, new SimpleHttpClientFactory(), TimeSpan.FromMilliseconds(20));
        await poller.StartAsync(ct);
        try
        {
            for (var i = 0; i < 500 && !TrackedInvoiceRegistry.TryGetResult(hash, out _); i++) await Task.Delay(10, ct);
            Assert.True(TrackedInvoiceRegistry.TryGetResult(hash, out var got));
            Assert.Equal(LightningInvoiceStatus.Unpaid, got!.Status);
        }
        finally
        {
            await poller.StopAsync(ct);
            LNURLVerifyPollerService.PollOverride = null;
            TrackedInvoiceRegistry.Remove(hash);
        }
    }

    [Fact]
    public async Task Invoices_and_destinations_at_one_endpoint_share_one_batch()
    {
        var host = NewHost("mixed");
        var hash = NewHash();
        var d = Destination(host);
        TrackedInvoiceRegistry.Add(Batched(host, hash));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, url => url == d.VerifyUrl ? DestinationState(false) : Pending);
        var expected = new[] { d.VerifyUrl, $"https://{host}/lnurl/verify/{hash}" }.OrderBy(u => u, StringComparer.Ordinal).ToArray();

        await Tracking(new[] { d }, () => Polling(NewPoller(http, 30), new[] { hash }, async () =>
        {
            await Until(() => server.Batches.Count >= 2);
            Assert.All(server.Batches, b => Assert.Equal(expected, b.OrderBy(u => u, StringComparer.Ordinal)));
            Assert.Empty(server.Singles);
            Assert.True(TrackedDestinationRegistry.IsTracked(d.VerifyUrl));
        }));
    }

    [Fact]
    public async Task The_poller_forwards_no_payer_ip()
    {
        var host = NewHost("xff");
        var hash = NewHash();
        var d = Destination(host, batched: false);
        TrackedInvoiceRegistry.Add(Batched(host, hash));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => Pending, _ => DestinationState(false));
        // Started beside a checkout, so its loop inherits the payer's IP.
        PayerIp.Current = IPAddress.Parse("203.0.113.7");

        await Tracking(new[] { d }, () => Polling(NewPoller(http, 30), new[] { hash }, async () =>
        {
            await Until(() => !server.Batches.IsEmpty && server.Singles.Contains(d.VerifyUrl));
            Assert.All(http.ForwardedFor, v => Assert.Null(v));
        }));
    }

    [Fact]
    public async Task A_destination_settled_with_a_reference_is_reported_and_untracked()
    {
        var host = NewHost("paid");
        var d = Destination(host);
        var http = new FakeHttp();
        _ = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => DestinationState(true, "arktxid"));

        await Tracking(new[] { d }, async () => Assert.Equal("arktxid", await Observe(
            h => TrackedDestinationRegistry.Settled += h, h => TrackedDestinationRegistry.Settled -= h, d,
            () => Polling(NewPoller(http, 30), Array.Empty<string>(), () => Until(() => !TrackedDestinationRegistry.IsTracked(d.VerifyUrl))))));
    }

    [Fact]
    public async Task A_destination_settled_without_a_reference_stays_tracked()
    {
        var host = NewHost("noref");
        var d = Destination(host);
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => DestinationState(true));

        await Tracking(new[] { d }, () => Polling(NewPoller(http, 30), Array.Empty<string>(), async () =>
        {
            await Until(() => server.Batches.Count >= 3);
            Assert.True(TrackedDestinationRegistry.IsTracked(d.VerifyUrl));
        }));
    }

    [Fact]
    public async Task A_destination_the_service_no_longer_knows_is_untracked_and_reported()
    {
        var host = NewHost("forgot");
        var d = Destination(host);
        var http = new FakeHttp();
        _ = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => "{\"status\":\"ERROR\",\"reason\":\"Not found\"}");

        await Tracking(new[] { d }, async () => Assert.Equal("Not found", await Observe(
            h => TrackedDestinationRegistry.Forgotten += h, h => TrackedDestinationRegistry.Forgotten -= h, d,
            () => Polling(NewPoller(http, 30), Array.Empty<string>(), () => Until(() => !TrackedDestinationRegistry.IsTracked(d.VerifyUrl))))));
    }

    [Fact]
    public async Task A_destination_without_verifyBatch_is_polled_on_its_own()
    {
        var host = NewHost("single");
        var d = Destination(host, batched: false);
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => DestinationState(true, "arktxid"));

        await Tracking(new[] { d }, async () => Assert.Equal("arktxid", await Observe(
            h => TrackedDestinationRegistry.Settled += h, h => TrackedDestinationRegistry.Settled -= h, d,
            () => Polling(NewPoller(http, 30), Array.Empty<string>(), () => Until(() => !TrackedDestinationRegistry.IsTracked(d.VerifyUrl))))));
        Assert.Contains(d.VerifyUrl, server.Singles);
        Assert.Empty(server.Batches);
    }

    [Fact]
    public async Task Expired_destinations_are_dropped_before_polling()
    {
        var host = NewHost("expdest");
        var (live, expired) = (Destination(host), Destination(host, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        var http = new FakeHttp();
        var server = new FakeBatchServer(http, host, _ => HttpStatusCode.OK, _ => DestinationState(false));

        await Tracking(new[] { live, expired }, () => Polling(NewPoller(http, 30), Array.Empty<string>(), async () =>
        {
            await Until(() => server.Batches.Count >= 2);
            Assert.All(server.Batches, b => Assert.DoesNotContain(expired.VerifyUrl, b));
            Assert.False(TrackedDestinationRegistry.IsTracked(expired.VerifyUrl));
        }));
    }
}

/// <summary>A verifyBatch-capable LNURL service on one host, recording every request it receives.</summary>
sealed class FakeBatchServer
{
    public readonly ConcurrentQueue<string[]> Batches = new();
    public readonly ConcurrentQueue<string> Singles = new();

    public FakeBatchServer(FakeHttp http, string host, Func<string[], HttpStatusCode> status, Func<string, string?> item,
        Func<string, string?>? single = null)
    {
        single ??= item;
        http.When(r => r.RequestUri!.Host == host && r.RequestUri.AbsolutePath == "/lnurl/verifyBatch", r =>
        {
            var urls = FakeHttp.VerifyParams(r);
            Batches.Enqueue(urls);
            var code = status(urls);
            if (code != HttpStatusCode.OK) return (code, "{}");
            var results = new JObject();
            foreach (var url in urls)
                if (item(url) is { } json) results[url] = JObject.Parse(json);
            return (HttpStatusCode.OK, new JObject { ["status"] = "OK", ["results"] = results }.ToString());
        });
        http.When(r => r.RequestUri!.Host == host && r.RequestUri.AbsolutePath.StartsWith("/lnurl/verify/"), r =>
        {
            Singles.Enqueue(r.RequestUri!.ToString());
            return (HttpStatusCode.OK, single(r.RequestUri.ToString()) ?? "{\"status\":\"ERROR\",\"reason\":\"Not found\"}");
        });
    }
}
