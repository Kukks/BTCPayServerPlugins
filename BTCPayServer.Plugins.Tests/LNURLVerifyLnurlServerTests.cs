using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using BTCPayServer.Plugins.LNURLVerify.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Tests.LNURLVerify;

// The plugin against a real lnurl-server (>= a4150f7, which serves verifyBatch). The test is the wallet: it
// answers invoice requests with self-signed regtest invoices and reports their preimages, which is all
// lnurl-server needs to settle them, so no Lightning node is involved. See VERIFICATION.md to run it.
[Trait("Integration", "LnurlServer")]
public class LNURLVerifyLnurlServerTests
{
    [Fact]
    public async Task Settles_through_lnurl_server_verifyBatch_without_per_invoice_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseUrl = Environment.GetEnvironmentVariable("LNURL_SERVER_URL")?.TrimEnd('/')
                      ?? throw new InvalidOperationException("Set LNURL_SERVER_URL to a running lnurl-server (see VERIFICATION.md).");
        using var wallet = await LnurlServerWallet.Open(baseUrl, ct);
        var requests = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(requests, false) { Timeout = TimeSpan.FromSeconds(30) };

        var resolved = await LNURLResolver.Resolve($"{baseUrl}/lnurl/{wallet.SessionId}", Network.RegTest, http, ct);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http, NullLogger.Instance);
        var hashes = new List<string>();
        for (var i = 0; i < 3; i++)
            hashes.Add((await rx.CreateInvoice(LightMoney.Satoshis(1000 + i), "lnurl-server e2e", null, ct)).PaymentHash!);
        try
        {
            foreach (var hash in hashes)
            {
                Assert.True(TrackedInvoiceRegistry.TryGet(hash, out var t));
                Assert.Equal($"{baseUrl}/lnurl/verifyBatch", t.VerifyBatch);
            }

            var settle = new[] { hashes[0], hashes[2] };
            using var listener = new LNURLVerifyListener(t => settle.Contains(t.PaymentHash));
            // 1 s keeps the poller well inside lnurl-server's 120 verify requests/min per IP.
            var poller = new LNURLVerifyPollerService(NullLogger<LNURLVerifyPollerService>.Instance,
                new PerCallClientFactory(requests), TimeSpan.FromSeconds(1));
            await poller.StartAsync(ct);
            try
            {
                foreach (var hash in settle) await wallet.ReportSettled(hash, ct);
                var preimages = new Dictionary<string, string?>();
                while (preimages.Count < settle.Length)
                {
                    var paid = await listener.WaitInvoice(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
                    preimages[paid.PaymentHash!] = paid.Preimage;
                }
                foreach (var hash in settle) Assert.Equal(wallet.PreimageOf(hash), preimages[hash]);
            }
            finally
            {
                await poller.StopAsync(ct);
            }

            Assert.Equal(0, requests.Count("/lnurl/verify/"));
            Assert.True(requests.Count("/lnurl/verifyBatch") > 0);
            var before = requests.Total;
            Assert.Equal(LightningInvoiceStatus.Unpaid, (await rx.GetInvoice(hashes[1], ct))!.Status);
            Assert.Equal(before, requests.Total);
        }
        finally
        {
            foreach (var hash in hashes) TrackedInvoiceRegistry.Remove(hash);
        }
    }

    // Not an assertion: holds alice@<server> open (Lightning answered, Arkade identity set) for VERIFICATION.md §5.
    [Fact]
    public async Task Serves_an_arkade_address_for_the_checkout_runbook()
    {
        var ct = TestContext.Current.CancellationToken;
        using var wallet = await LnurlServerWallet.Open(Required("LNURL_SERVER_URL").TrimEnd('/'), ct);
        await wallet.RegisterAddress("alice", Required("LNURL_ARKADE_ADDRESS"), new Key().PubKey.ToHex(),
            Environment.GetEnvironmentVariable("LNURL_BOARDING_ADDRESS"), ct);
        await Task.Delay(TimeSpan.FromMinutes(double.Parse(Required("LNURL_RUNBOOK_MINUTES"), CultureInfo.InvariantCulture)), ct);
    }

    static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name} (see VERIFICATION.md).");
}

/// <summary>An lnurl-server wallet session that mints self-signed invoices and reports their preimages.</summary>
file sealed class LnurlServerWallet : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _stop = new();
    private readonly Key _nodeKey = new();
    private readonly ConcurrentDictionary<string, string> _preimages = new();
    private readonly string _baseUrl;
    private string _token = "";

    private LnurlServerWallet(string baseUrl) => _baseUrl = baseUrl;

    public string SessionId { get; private set; } = "";

    public static async Task<LnurlServerWallet> Open(string baseUrl, CancellationToken ct)
    {
        var wallet = new LnurlServerWallet(baseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/lnurl/session")
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        var response = await wallet._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var events = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var (name, data) = await NextEvent(events, ct);
        if (name != "session_created") throw new InvalidOperationException($"expected session_created, got {name}");
        wallet.SessionId = data["sessionId"]!.Value<string>()!;
        wallet._token = data["token"]!.Value<string>()!;
        _ = Task.Run(() => wallet.AnswerInvoiceRequests(events, wallet._stop.Token));
        return wallet;
    }

    private async Task AnswerInvoiceRequests(StreamReader events, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (name, data) = await NextEvent(events, ct);
                if (name != "invoice_request") continue;
                var preimage = RandomNumberGenerator.GetBytes(32);
                var hash = SHA256.HashData(preimage);
                _preimages[Convert.ToHexString(hash).ToLowerInvariant()] = Convert.ToHexString(preimage).ToLowerInvariant();
                var pr = TestBolt11.Create(_nodeKey, data["amountMsat"]!.Value<long>(), hash);
                await Post($"/lnurl/session/{SessionId}/invoice", new JObject { ["pr"] = pr }, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    public Task ReportSettled(string paymentHash, CancellationToken ct) =>
        Post($"/lnurl/session/{SessionId}/settled", new JObject { ["preimage"] = _preimages[paymentHash] }, ct);

    public string PreimageOf(string paymentHash) => _preimages[paymentHash];

    public async Task RegisterAddress(string username, string arkadeAddress, string claimPublicKey, string? boardingAddress,
        CancellationToken ct)
    {
        await Post("/lnurl/address", new JObject { ["token"] = _token, ["username"] = username }, ct);
        var identity = new JObject { ["arkadeAddress"] = arkadeAddress, ["claimPublicKey"] = claimPublicKey };
        if (boardingAddress is not null) identity["boardingAddress"] = boardingAddress;
        await Post($"/lnurl/address/{username}/arkade", identity, ct);
    }

    private async Task Post(string path, JObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<(string Name, JObject Data)> NextEvent(StreamReader events, CancellationToken ct)
    {
        var name = "message";
        var data = new StringBuilder();
        while (true)
        {
            var line = await events.ReadLineAsync(ct) ?? throw new EndOfStreamException("lnurl-server closed the session stream");
            if (line.Length == 0)
            {
                if (data.Length > 0) return (name, JObject.Parse(data.ToString()));
                name = "message";
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line.Substring(6).Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line.Substring(5).Trim());
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}

file sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly ConcurrentQueue<string> _paths = new();

    public int Total => _paths.Count;

    public int Count(string pathPrefix) => _paths.Count(p => p.StartsWith(pathPrefix, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _paths.Enqueue(request.RequestUri!.AbsolutePath);
        return base.SendAsync(request, ct);
    }
}

// A fresh client per call over one shared handler: the poller sets Timeout on every client it is given.
file sealed class PerCallClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
