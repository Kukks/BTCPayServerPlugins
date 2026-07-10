using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LNURLReceiverTests
{
    [Fact]
    public void Preimage_validation_matches_sha256()
    {
        var preimage = new string('0', 64); // 32 zero bytes
        var hash = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"; // sha256(32*0x00)
        Assert.True(LNURLReceiver.IsValidPreimage(preimage, hash));
        Assert.False(LNURLReceiver.IsValidPreimage(preimage, new string('1', 64)));
        Assert.False(LNURLReceiver.IsValidPreimage("xyz", hash));
        Assert.False(LNURLReceiver.IsValidPreimage(null, hash));
    }

    [Fact]
    public async Task GetInvoice_transient_transport_error_returns_unpaid_not_null()
    {
        var host = "rx.example";
        var hash = new string('a', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}", "{}", HttpStatusCode.InternalServerError);
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http.Client(), NullLogger.Instance);

        var inv = await rx.GetInvoice(hash, CancellationToken.None);

        Assert.NotNull(inv);
        Assert.Equal(LightningInvoiceStatus.Unpaid, inv!.Status);
        TrackedInvoiceRegistry.Remove(hash);
    }

    [Fact]
    public async Task GetInvoice_error_status_returns_null()
    {
        var host = "rx2.example";
        var hash = new string('b', 64);
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay",
            DateTimeOffset.UtcNow.AddHours(1)));
        var http = new FakeHttp().Map($"https://{host}/verify/{hash}", "{\"status\":\"ERROR\",\"reason\":\"nope\"}");
        var resolved = new ResolvedLnurl(LnurlCapability.ReceiveOnly, new Uri($"https://{host}/pay"), null, null, host);
        var rx = new LNURLReceiver(resolved, Network.RegTest, http.Client(), NullLogger.Instance);

        var inv = await rx.GetInvoice(hash, CancellationToken.None);

        Assert.Null(inv);
        TrackedInvoiceRegistry.Remove(hash);
    }
}
