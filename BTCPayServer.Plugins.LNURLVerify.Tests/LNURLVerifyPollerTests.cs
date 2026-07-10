using System;
using System.Collections.Generic;
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
        await poller.StartAsync(default);
        try
        {
            for (int i = 0; i < 300 && Volatile.Read(ref settled) < 30; i++)
                await Task.Delay(25);

            Assert.Equal(30, Volatile.Read(ref settled));                 // all 30 even invoices settled once
            foreach (var h in hashes)
                if (int.Parse(h.Substring(4, 2)) % 2 == 0)
                    Assert.True(TrackedInvoiceRegistry.TryGetSettled(h, out _)); // retrievable as Paid
        }
        finally
        {
            await poller.StopAsync(default);
            LNURLVerifyPollerService.PollOverride = null;
            TrackedInvoiceRegistry.Settled -= Handler;
            foreach (var h in hashes) TrackedInvoiceRegistry.Remove(h);
            TrackedInvoiceRegistry.PruneSettled(DateTimeOffset.UtcNow.AddDays(1));
        }
    }
}
