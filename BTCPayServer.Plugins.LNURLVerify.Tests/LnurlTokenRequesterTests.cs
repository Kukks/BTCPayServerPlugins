using System.Net;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class LnurlTokenRequesterTests
{
    const string Callback = "https://lnurl.example/cb";
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
    static readonly DateTimeOffset InvoiceExpiry = Now.AddMinutes(15);

    static JObject Pay(string extra = "") => JObject.Parse(
        "{\"tag\":\"payRequest\",\"callback\":\"" + Callback + "\",\"minSendable\":1000,\"maxSendable\":1000000000,\"metadata\":\"[]\"," +
        "\"units\":[{\"code\":\"USDT\",\"decimals\":6}],\"paymentOptions\":[{\"id\":\"usdt-arbitrum\",\"type\":\"eip155\"," +
        "\"asset\":\"eip155:42161/erc20:0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9\",\"unit\":\"USDT\"" + extra + "}]}");

    static JObject Answer() => JObject.Parse(
        "{\"paymentOption\":\"usdt-arbitrum\",\"paymentDestination\":\"0x1111111111111111111111111111111111111111\"," +
        "\"paymentQuote\":{\"requested\":{\"amount\":\"100000000\",\"unit\":\"msat\"},\"payment\":{\"amount\":\"63360000\",\"unit\":\"USDT\"},\"expiresAt\":\"1790000600\"}," +
        "\"verify\":\"https://lnurl.example/lnurl/verify/1\",\"verifyBatch\":\"https://lnurl.example/lnurl/verifyBatch\",\"expiresAt\":1790600000}");

    static FakeHttp Serving(JObject answer) =>
        new FakeHttp().When(r => r.RequestUri!.ToString().StartsWith(Callback + "?"), _ => (HttpStatusCode.OK, answer.ToString()));

    static Task<TokenQuote> Request(JObject answer, JObject? pay = null, long msat = 100_000_000, FakeHttp? http = null)
    {
        pay ??= Pay();
        return LnurlTokenRequester.Request((http ?? Serving(answer)).Client(), pay, TokenOption.Parse(pay).Single(), msat, InvoiceExpiry, Now,
            TestContext.Current.CancellationToken);
    }

    static void Spoil(JObject answer, string how)
    {
        var quote = (JObject)answer["paymentQuote"]!;
        var payment = (JObject)quote["payment"]!;
        switch (how)
        {
            case "exponent amount": payment["amount"] = "6.336e7"; break;
            case "zero amount": payment["amount"] = "0"; break;
            case "another unit": payment["unit"] = "USDC"; break;
            case "no quote": answer.Remove("paymentQuote"); break;
            case "quote not an object": answer["paymentQuote"] = "63360000"; break;
            case "expired quote": quote["expiresAt"] = 1_790_000_000; break;
            case "unreadable expiry": quote["expiresAt"] = "soon"; break;
            case "another option": answer["paymentOption"] = "usdt-tron"; break;
            case "no destination": answer.Remove("paymentDestination"); break;
            case "destination with markup": answer["paymentDestination"] = "0x1111<script>"; break;
            case "tron destination for an evm option": answer["paymentDestination"] = "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7"; break;
            case "destination expiring before the invoice": answer["expiresAt"] = 1_790_000_300; break;
            default: throw new ArgumentOutOfRangeException(nameof(how));
        }
    }

    [Fact]
    public async Task A_quoted_network_returns_its_destination_amount_and_expiry() =>
        Assert.Equal(new TokenQuote("usdt-arbitrum", "0x1111111111111111111111111111111111111111", "63360000", 1_790_000_600,
                "https://lnurl.example/lnurl/verify/1", "https://lnurl.example/lnurl/verifyBatch", 100_000_000),
            await Request(Answer()));

    [Fact]
    public async Task The_callback_names_the_option_and_the_msat_amount()
    {
        var http = Serving(Answer());
        await Request(Answer(), http: http);
        Assert.Equal(new[] { Callback + "?amount=100000000&paymentOption=usdt-arbitrum" }, http.Requests);
    }

    [Fact]
    public async Task A_quote_amount_may_be_a_json_number()
    {
        var answer = Answer();
        answer["paymentQuote"]!["payment"]!["amount"] = 63_360_000;
        Assert.Equal("63360000", (await Request(answer)).Amount);
    }

    [Theory]
    [InlineData("\"1790000600\"")]
    [InlineData("1790000600")]
    [InlineData("\"2026-09-21T14:23:20Z\"")]
    [InlineData("\"2026-09-21T14:23:20\"")]
    [InlineData("\"2026-09-21T16:23:20+02:00\"")]
    public async Task A_quote_expiry_is_read_as_unix_seconds_or_iso_8601(string json)
    {
        var answer = Answer();
        answer["paymentQuote"]!["expiresAt"] = JToken.Parse(json);
        Assert.Equal(1_790_000_600, (await Request(answer)).ExpiresAt);
    }

    [Fact]
    public async Task A_quote_without_an_expiry_never_expires()
    {
        var answer = Answer();
        ((JObject)answer["paymentQuote"]!).Remove("expiresAt");
        Assert.Null((await Request(answer)).ExpiresAt);
    }

    [Theory]
    [InlineData("exponent amount")]
    [InlineData("zero amount")]
    [InlineData("another unit")]
    [InlineData("no quote")]
    [InlineData("quote not an object")]
    [InlineData("expired quote")]
    [InlineData("unreadable expiry")]
    [InlineData("another option")]
    [InlineData("no destination")]
    [InlineData("destination with markup")]
    [InlineData("tron destination for an evm option")]
    [InlineData("destination expiring before the invoice")]
    public async Task An_answer_the_checkout_could_not_show_or_settle_is_refused(string how)
    {
        var answer = Answer();
        Spoil(answer, how);
        await Assert.ThrowsAnyAsync<PaymentMethodUnavailableException>(() => Request(answer));
    }

    [Fact]
    public async Task A_quote_amount_that_is_not_an_integer_is_refused()
    {
        var answer = Answer();
        answer["paymentQuote"]!["payment"]!["amount"] = "63.36";
        var e = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Request(answer));
        Assert.Contains("no whole amount of USDT", e.Message);
    }

    [Fact]
    public async Task A_network_without_verify_is_refused_as_unverifiable()
    {
        var answer = Answer();
        answer.Remove("verify");
        await Assert.ThrowsAsync<UnverifiableRailException>(() => Request(answer));
    }

    [Fact]
    public async Task A_network_marked_unverifiable_is_refused_without_a_callback()
    {
        var http = Serving(Answer());
        await Assert.ThrowsAsync<UnverifiableRailException>(() => Request(Answer(), Pay(",\"verifiable\":false"), http: http));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task An_amount_outside_the_network_bounds_is_refused_without_a_callback()
    {
        var http = Serving(Answer());
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Request(Answer(), msat: 1_000_000_001, http: http));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task An_unavailable_network_is_refused_without_a_callback()
    {
        var http = Serving(Answer());
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Request(Answer(), Pay(",\"available\":false"), http: http));
        Assert.Empty(http.Requests);
    }
}
