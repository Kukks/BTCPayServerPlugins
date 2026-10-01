using System.Globalization;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailRecorderTests
{
    [Fact]
    public void One_transaction_paying_two_destinations_records_two_payments()
    {
        var a = LnurlRailRecorder.PaymentId("arktxid", "https://lnurl.example/lnurl/verify/aa");
        var b = LnurlRailRecorder.PaymentId("arktxid", "https://lnurl.example/lnurl/verify/bb");
        Assert.NotEqual(a, b);
        Assert.Equal(a, LnurlRailRecorder.PaymentId("arktxid", "https://lnurl.example/lnurl/verify/aa"));
        Assert.Matches("^arktxid:[0-9a-f]{16}$", a);
    }

    [Theory]
    [InlineData("0.0005", 50_000_000, false)]
    [InlineData("0.0003", 50_000_000, true)]
    [InlineData("0", 50_000_000, false)]
    public void Only_a_changed_due_reissues_a_destination(string due, long agreedMsat, bool expected) =>
        Assert.Equal(expected, LnurlRailRecorder.NeedsReissue(decimal.Parse(due, CultureInfo.InvariantCulture), agreedMsat));
}
