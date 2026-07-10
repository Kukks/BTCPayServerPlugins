using System;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LNURLVerify;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TrackedInvoiceRegistryTests
{
    static TrackedInvoice Mk(string hash, string host) =>
        new(hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void Add_get_remove_roundtrips()
    {
        var t = Mk("reg_aa", "reg1.example");
        TrackedInvoiceRegistry.Add(t);
        Assert.True(TrackedInvoiceRegistry.TryGet("reg_aa", out var got));
        Assert.Equal(t.VerifyHost, got.VerifyHost);
        Assert.Equal(t.PayEndpoint, got.PayEndpoint);
        Assert.Contains("reg1.example", TrackedInvoiceRegistry.Hosts());

        TrackedInvoiceRegistry.Remove("reg_aa");
        Assert.False(TrackedInvoiceRegistry.TryGet("reg_aa", out _));
        // The now-empty host bucket is intentionally NOT pruned (pruning races a concurrent Add for the
        // same host and can orphan it), but the invoice itself is gone.
        Assert.Empty(TrackedInvoiceRegistry.SnapshotByHost("reg1.example"));
    }

    [Fact]
    public void Snapshot_groups_by_host()
    {
        TrackedInvoiceRegistry.Add(Mk("reg_b1", "reg2.example"));
        TrackedInvoiceRegistry.Add(Mk("reg_b2", "reg2.example"));
        Assert.Equal(2, TrackedInvoiceRegistry.SnapshotByHost("reg2.example").Count);
        TrackedInvoiceRegistry.Remove("reg_b1");
        TrackedInvoiceRegistry.Remove("reg_b2");
        Assert.Empty(TrackedInvoiceRegistry.SnapshotByHost("reg2.example"));
    }

    [Fact]
    public void MarkSettled_is_retrievable_within_grace_and_removed_from_tracked()
    {
        var hash = "reg_settle";
        TrackedInvoiceRegistry.Add(Mk(hash, "regsettle.example"));
        var paid = new LightningInvoice { Id = hash, PaymentHash = hash, Status = LightningInvoiceStatus.Paid };

        TrackedInvoiceRegistry.MarkSettled(hash, paid, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.False(TrackedInvoiceRegistry.TryGet(hash, out _));          // no longer polled
        Assert.True(TrackedInvoiceRegistry.TryGetSettled(hash, out var got));
        Assert.Equal(LightningInvoiceStatus.Paid, got.Status);

        // Past the grace window it is no longer retrievable (so BTCPay only ever gets null once it has
        // long since recorded the payment).
        TrackedInvoiceRegistry.MarkSettled(hash, paid, DateTimeOffset.UtcNow.AddMilliseconds(-1));
        Assert.False(TrackedInvoiceRegistry.TryGetSettled(hash, out _));
        TrackedInvoiceRegistry.PruneSettled(DateTimeOffset.UtcNow);
    }
}
