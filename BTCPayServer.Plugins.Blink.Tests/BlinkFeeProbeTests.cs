using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blink;
using NBitcoin;
using Xunit;

// Tests for the fee-probe branch decision in the custodial client's Pay path.
// Amount-carrying invoices must be probed (so Blink caches the route and settles at the
// exact fee instead of its max fee reserve); zero-amount invoices carry no amount to probe
// with here and must be paid as-is (ShouldProbeFee == false).
public class BlinkFeeProbeTests
{
    // Amount-carrying mainnet invoices (from BlinkLightningClient.cs fixtures):
    // 1u = 100 sat, 10n = 1 sat.
    private const string AmountInvoice100Sat =
        "lnbc1u1p5hn2dmpp5mcc880zq4jal99yztxmu2csjc83r7qcv6yfmfzd2fqzm5348w2ascqzyssp5z3lfcr54z3ssd93q2jyux2lah3v2ay7xs5fnd32j0pa3tfqrehgq9q7sqqqqqqqqqqqqqqqqqqqsqqqqqysgqdq2w3jhxapjxgmqz9gxqyjw5qrzjqwryaup9lh50kkranzgcdnn2fgvx390wgj5jd07rwr3vxeje0glcll7fwunuepcqyyqqqqlgqqqqqeqqjqmwuxa4w3ushqy5687v79zf8zrh3fyhwc7d863j0lu8lj7xf48wl8mtvxz56rr6r8sy3u69e7xndhdrrw6k9vuzd98yw56u07d268d4gq5zj8u0";

    private const string AmountInvoice1Sat =
        "lnbc10n1pjn9nmzpp5a7znt4tv9gy5v6342xrgnntltkljffp255dph40vaf6964j27pvqhp59ly2g7flsy97vqahh9yue8qz7u6tvlpjfh9r0m9nzfezhm6fgmqscqzzsxqzursp55l9zht4zya3jyjdr9khr22z6afvjqdcw06l7vyd6tksdtsc8ezqs9qyyssqwswp9dnz9txv8t8zjrrts9rv4agu40ufqc04434f6lszdwvlhjk45m3pdcpqzghswkrcvgeaztcr6h82xp35suu64hnk4ms929pcahgpfg7sza";

    // Zero-amount mainnet invoice (BOLT11 spec test vector: "Please consider supporting this
    // project" donation invoice — no amount encoded in the human-readable part 'lnbc1...').
    private const string ZeroAmountInvoice =
        "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    [Fact]
    public void ShouldProbeFee_true_for_amount_invoice_100sat()
    {
        var bolt11 = BOLT11PaymentRequest.Parse(AmountInvoice100Sat, Network.Main);
        Assert.True(BlinkLightningClient.ShouldProbeFee(bolt11));
    }

    [Fact]
    public void ShouldProbeFee_true_for_amount_invoice_1sat()
    {
        var bolt11 = BOLT11PaymentRequest.Parse(AmountInvoice1Sat, Network.Main);
        Assert.True(BlinkLightningClient.ShouldProbeFee(bolt11));
    }

    [Fact]
    public void ShouldProbeFee_false_for_zero_amount_invoice()
    {
        var bolt11 = BOLT11PaymentRequest.Parse(ZeroAmountInvoice, Network.Main);
        Assert.False(BlinkLightningClient.ShouldProbeFee(bolt11));
    }
}
