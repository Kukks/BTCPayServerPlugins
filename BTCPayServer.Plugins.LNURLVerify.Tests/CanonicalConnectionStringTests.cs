using System.Linq;
using System.Net;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class CanonicalConnectionStringTests
{
    const string Onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    static string Bech32(string url) =>
        LNURL.LNURL.EncodeUri(new Uri(url), "payRequest", true).ToString()["lightning:".Length..];

    [Fact]
    public void A_lightning_address_canonicalises_to_its_well_known_origin() =>
        Assert.Equal("type=lnurl;server=https://h.example;value=alice@h.example",
            LnurlRailProvisioning.ConnectionString("alice@h.example"));

    [Fact]
    public void A_bech32_lnurl_canonicalises_to_its_decoded_origin()
    {
        var bech32 = Bech32("https://lnurl.example/pay/x");

        Assert.Equal($"type=lnurl;server=https://lnurl.example;value={bech32}",
            LnurlRailProvisioning.ConnectionString(bech32));
    }

    [Fact]
    public void A_lightning_prefixed_lnurl_canonicalises_to_the_same_origin()
    {
        var bech32 = Bech32("https://lnurl.example/pay/x");

        Assert.Equal($"type=lnurl;server=https://lnurl.example;value=lightning:{bech32}",
            LnurlRailProvisioning.ConnectionString("lightning:" + bech32));
    }

    [Fact]
    public void A_lud17_scheme_canonicalises_to_its_https_origin() =>
        Assert.Equal("type=lnurl;server=https://h.example;value=lnurlp://h.example/pay/x",
            LnurlRailProvisioning.ConnectionString("lnurlp://h.example/pay/x"));

    // LUD-16/17 put onion LNURLs on http, so the origin must keep that scheme rather than assume https.
    [Fact]
    public void An_onion_address_canonicalises_to_an_http_origin() =>
        Assert.Equal($"type=lnurl;server=http://{Onion};value=alice@{Onion}",
            LnurlRailProvisioning.ConnectionString($"alice@{Onion}"));

    [Fact]
    public void A_non_default_port_stays_in_the_origin() =>
        Assert.Equal("type=lnurl;server=http://10.0.0.5:8080;value=http://10.0.0.5:8080/pay",
            LnurlRailProvisioning.ConnectionString("http://10.0.0.5:8080/pay"));

    [Fact]
    public void The_canonical_string_still_reads_back_as_its_value() =>
        Assert.Equal("alice@h.example",
            LnurlRailProvisioning.LnurlValue(LnurlRailProvisioning.ConnectionString("alice@h.example")));

    [Fact]
    public void A_server_fronting_a_different_value_is_refused_before_any_request()
    {
        var fake = new FakeHttp();
        var handler = new LNURLVerifyConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var client = handler.Create("type=lnurl;server=https://public.example;value=alice@internal-host",
            Network.RegTest, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
        Assert.Empty(fake.Requests);
    }

    [Theory]
    [InlineData("https://h.example:8443")]
    [InlineData("http://h.example")]
    [InlineData("https://other.example")]
    public void A_server_differing_in_scheme_host_or_port_is_refused(string server)
    {
        var fake = new FakeHttp();
        var handler = new LNURLVerifyConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        Assert.Null(handler.Create($"type=lnurl;server={server};value=alice@h.example", Network.RegTest, out var error));
        Assert.NotNull(error);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void A_matching_server_resolves_and_its_host_casing_is_ignored()
    {
        var host = "canon" + Guid.NewGuid().ToString("N")[..8] + ".example";
        var fake = new FakeHttp().Map($"https://{host}/.well-known/lnurlp/alice",
            "{\"tag\":\"payRequest\",\"callback\":\"https://" + host + "/cb\",\"minSendable\":1000,\"maxSendable\":100000,\"metadata\":\"[]\"}");
        var handler = new LNURLVerifyConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        var client = handler.Create($"type=lnurl;server=https://{host.ToUpperInvariant()};value=alice@{host}",
            Network.RegTest, out var error);

        Assert.Null(error);
        Assert.NotNull(client);
    }

    [Fact]
    public void A_legacy_string_without_a_server_still_resolves()
    {
        var host = "legacy" + Guid.NewGuid().ToString("N")[..8] + ".example";
        var fake = new FakeHttp().Map($"https://{host}/pay",
            "{\"tag\":\"payRequest\",\"callback\":\"https://" + host + "/cb\",\"minSendable\":1000,\"maxSendable\":100000,\"metadata\":\"[]\"}");
        var handler = new LNURLVerifyConnectionStringHandler(new FakeHttpClientFactory(fake), NullLoggerFactory.Instance);

        Assert.NotNull(handler.Create($"type=lnurl;value=https://{host}/pay", Network.RegTest, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_public_canonical_string_passes_core_2_4_5_but_a_private_or_legacy_one_does_not()
    {
        Assert.True(IsSafeInCore245(LnurlRailProvisioning.ConnectionString("alice@h.example")));
        Assert.True(IsSafeInCore245(LnurlRailProvisioning.ConnectionString($"alice@{Onion}")));
        Assert.False(IsSafeInCore245(LnurlRailProvisioning.ConnectionString("http://127.0.0.1:1234/pay")));
        Assert.False(IsSafeInCore245(LnurlRailProvisioning.ConnectionString("alice@lnurl.local")));
        Assert.False(IsSafeInCore245("type=lnurl;value=alice@h.example"));
    }

    /// <summary>
    /// Copied from core commit 8e96a65 "Harden Lightning connection validation (#7635)" (v2.4.5),
    /// BTCPayServer/Extensions.cs. The pinned submodule predates it, so its own IsSafe still returns
    /// true for a string with no server and cannot stand in for this.
    /// </summary>
    static bool IsSafeInCore245(string connectionString)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (kv.Keys.Any(k => k.EndsWith("filepath", StringComparison.OrdinalIgnoreCase) ||
                             k.EndsWith("directorypath", StringComparison.OrdinalIgnoreCase)))
            return false;
        var isLnd = type is "lnd-rest" or "lnd-grpc";
        if (isLnd && kv.ContainsKey("certthumbprint"))
            return false;
        if (!kv.TryGetValue("server", out var server))
            return false;
        var uri = new Uri(server, UriKind.Absolute);
        if (isLnd && uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            kv.TryGetValue("allowinsecure", out var allowInsecure) &&
            bool.TryParse(allowInsecure, out var enabled) && enabled)
            return false;
        if (uri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Utils.TryParseEndpoint(uri.DnsSafeHost, 80, out _))
            return false;
        if (!IPAddress.TryParse(uri.DnsSafeHost, out _) && uri.Scheme is not ("http" or "https"))
            return false;
        return !BTCPayServer.Extensions.IsLocalNetwork(uri.DnsSafeHost);
    }
}
