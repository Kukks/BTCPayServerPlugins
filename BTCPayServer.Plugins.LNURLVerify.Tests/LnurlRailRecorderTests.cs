using System.Globalization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlRailRecorderTests
{
    const string Evm = "0x1111111111111111111111111111111111111111";
    const string Tron = "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7";
    const string Verify = "https://lnurl.example/lnurl/verify/aa";
    const string Asset = "eip155:421614/erc20:0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB";

    // Core's PaymentData.Set stamps the prompt's destination, which on a token prompt is the last network requested.
    [Fact]
    public void A_payment_carries_the_destination_it_settles_not_the_prompts()
    {
        var handler = new LnurlTokenPaymentHandler("USDT", new FakeHttpClientFactory(new FakeHttp()), Network.RegTest, new TokenActivations());
        var invoice = new InvoiceEntity { Id = "inv", Currency = "BTC" };
        invoice.SetPaymentPrompt(handler.PaymentMethodId, new PaymentPrompt { Currency = "BTC", Divisibility = 8, Destination = Tron });
        var tracked = new TrackedDestination("inv", handler.PaymentMethodId.ToString(), Evm, Verify, null, 20_000_000,
            DateTimeOffset.UtcNow.AddDays(1), Asset);

        var data = LnurlRailRecorder.Payment(invoice, handler, tracked, "0xtx");
        var blob = data.GetBlob();

        Assert.Equal(Evm, blob.Destination);
        Assert.Equal(LnurlRailRecorder.PaymentId("0xtx", Verify), data.Id);
        Assert.Equal(("BTC", 0.0002m, "LNURL-USDT", PaymentStatus.Settled, 8, "inv"),
            (data.Currency, data.Amount!.Value, data.PaymentMethodId, data.Status!.Value, blob.Divisibility, data.InvoiceDataId));
        var details = handler.ParsePaymentDetails(blob.Details);
        Assert.Equal(("0xtx", Verify, Evm, Asset), (details.PaymentReference, details.VerifyUrl, details.Destination, details.Asset));
    }

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
