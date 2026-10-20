using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

[Collection(RegistryCollection.Name)]
public class TrackedDestinationRegistryTests
{
    static TrackedDestination NewDestination()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return new TrackedDestination("inv", "LNURL-ARKADE", "tark1q" + id, "https://lnurl.example/lnurl/verify/" + id, null,
            1_000_000, DateTimeOffset.UtcNow.AddHours(1));
    }

    [Fact]
    public void A_settlement_with_a_reference_is_reported_once()
    {
        var d = NewDestination();
        var seen = new List<string>();
        Action<TrackedDestination, string> handler = (x, reference) => { if (x == d) seen.Add(reference); };
        TrackedDestinationRegistry.Settled += handler;
        try
        {
            TrackedDestinationRegistry.Add(d);
            var answer = JObject.Parse("{\"status\":\"OK\",\"settled\":true,\"paymentReference\":\"arktxid\"}");
            TrackedDestinationRegistry.Apply(d, answer);
            TrackedDestinationRegistry.Apply(d, answer);
            Assert.Equal(new[] { "arktxid" }, seen);
            Assert.False(TrackedDestinationRegistry.IsTracked(d.VerifyUrl));
        }
        finally
        {
            TrackedDestinationRegistry.Settled -= handler;
            TrackedDestinationRegistry.Remove(d.VerifyUrl);
        }
    }

    [Theory]
    [InlineData("{\"status\":\"OK\",\"settled\":false,\"paymentReference\":null}")]
    [InlineData("{\"status\":\"OK\",\"settled\":true,\"paymentReference\":null}")]
    [InlineData("{\"status\":\"OK\",\"settled\":true,\"paymentReference\":\"\"}")]
    public void Without_a_settlement_and_a_reference_the_destination_stays_tracked(string answer)
    {
        var d = NewDestination();
        TrackedDestinationRegistry.Add(d);
        try
        {
            TrackedDestinationRegistry.Apply(d, JObject.Parse(answer));
            Assert.True(TrackedDestinationRegistry.IsTracked(d.VerifyUrl));
        }
        finally { TrackedDestinationRegistry.Remove(d.VerifyUrl); }
    }

    static string Reported(string paymentReference)
    {
        var d = NewDestination();
        string? seen = null;
        Action<TrackedDestination, string> handler = (x, reference) => { if (x == d) seen = reference; };
        TrackedDestinationRegistry.Settled += handler;
        try
        {
            TrackedDestinationRegistry.Add(d);
            TrackedDestinationRegistry.Apply(d, new JObject
                { ["status"] = "OK", ["settled"] = true, ["paymentReference"] = paymentReference });
            return seen!;
        }
        finally
        {
            TrackedDestinationRegistry.Settled -= handler;
            TrackedDestinationRegistry.Remove(d.VerifyUrl);
        }
    }

    [Fact]
    public void A_reference_core_would_render_unquoted_is_recorded_as_its_sha256()
    {
        const string hostile = "abc def\"><x";
        var txid = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostile))).ToLowerInvariant(), Reported(hostile));
        Assert.Equal(txid, Reported(txid));
    }

    [Fact]
    public void An_error_answer_untracks_and_is_reported_once()
    {
        var d = NewDestination();
        var seen = new List<string>();
        Action<TrackedDestination, string> handler = (x, reason) => { if (x == d) seen.Add(reason); };
        TrackedDestinationRegistry.Forgotten += handler;
        try
        {
            TrackedDestinationRegistry.Add(d);
            var answer = JObject.Parse("{\"status\":\"ERROR\",\"reason\":\"Not found\"}");
            TrackedDestinationRegistry.Apply(d, answer);
            TrackedDestinationRegistry.Apply(d, answer);
            Assert.Equal(new[] { "Not found" }, seen);
            Assert.False(TrackedDestinationRegistry.IsTracked(d.VerifyUrl));
        }
        finally
        {
            TrackedDestinationRegistry.Forgotten -= handler;
            TrackedDestinationRegistry.Remove(d.VerifyUrl);
        }
    }
}
