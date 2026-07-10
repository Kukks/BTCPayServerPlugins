using System;
using BTCPayServer.Plugins.LNURLVerify;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class TrackedInvoiceRegistryTests
{
    static TrackedInvoice Mk(string hash, string host) =>
        new(hash, "lnbc1", $"https://{host}/verify/{hash}", host, $"https://{host}/pay", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void Add_get_remove_roundtrips_and_prunes_host()
    {
        var t = Mk("reg_aa", "reg1.example");
        TrackedInvoiceRegistry.Add(t);
        Assert.True(TrackedInvoiceRegistry.TryGet("reg_aa", out var got));
        Assert.Equal(t.VerifyHost, got.VerifyHost);
        Assert.Equal(t.PayEndpoint, got.PayEndpoint);
        Assert.Contains("reg1.example", TrackedInvoiceRegistry.Hosts());

        TrackedInvoiceRegistry.Remove("reg_aa");
        Assert.False(TrackedInvoiceRegistry.TryGet("reg_aa", out _));
        Assert.DoesNotContain("reg1.example", TrackedInvoiceRegistry.Hosts());
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
}
