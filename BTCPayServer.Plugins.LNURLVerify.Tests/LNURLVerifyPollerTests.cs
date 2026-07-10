using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LNURLVerifyPollerTests
{
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
        var waiter = listener.WaitInvoice(CancellationToken.None);

        var poller = new LNURLVerifyPollerService(
            NullLogger<LNURLVerifyPollerService>.Instance, new SimpleHttpClientFactory(), TimeSpan.FromMilliseconds(20));
        await poller.StartAsync(default);
        try
        {
            var seen = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(hash, seen.PaymentHash);
            Assert.False(TrackedInvoiceRegistry.TryGet(hash, out _));
        }
        finally
        {
            await poller.StopAsync(default);
            LNURLVerifyPollerService.PollOverride = null;
        }
    }
}
