using System;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Blink;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

// Tests for the custodial ln-address mode: GraphQL endpoint derivation and the description-hash
// computation used to commit a directly-minted invoice to BTCPay's own LNURL metadata.
public class BlinkCustodialModeTests
{
    // --- GraphQLEndpointForDomain ------------------------------------------------------------

    [Fact]
    public void Blink_sv_maps_to_api_blink_sv()
    {
        Assert.Equal(new Uri("https://api.blink.sv/graphql"),
            BlinkGraphQLPublicClient.GraphQLEndpointForDomain("blink.sv"));
    }

    [Fact]
    public void Blink_sv_is_case_insensitive()
    {
        Assert.Equal(new Uri("https://api.blink.sv/graphql"),
            BlinkGraphQLPublicClient.GraphQLEndpointForDomain("Blink.SV"));
    }

    [Fact]
    public void Self_hosted_domain_gets_api_subdomain()
    {
        Assert.Equal(new Uri("https://api.pay.example.com/graphql"),
            BlinkGraphQLPublicClient.GraphQLEndpointForDomain("pay.example.com"));
    }

    [Fact]
    public void Domain_already_prefixed_with_api_is_not_doubled()
    {
        Assert.Equal(new Uri("https://api.example.com/graphql"),
            BlinkGraphQLPublicClient.GraphQLEndpointForDomain("api.example.com"));
    }

    [Fact]
    public void Localhost_uses_http()
    {
        Assert.Equal(new Uri("http://api.localhost/graphql"),
            BlinkGraphQLPublicClient.GraphQLEndpointForDomain("localhost"));
    }

    // --- ComputeDescriptionHashHex -----------------------------------------------------------

    private static string Sha256Hex(string s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    [Fact]
    public void DescriptionHashOnly_hashes_the_description_metadata()
    {
        var metadata = "[[\"text/plain\",\"BTCPay store\"]]";
        var req = new CreateInvoiceParams(LightMoney.Satoshis(21), metadata, TimeSpan.FromMinutes(15))
        {
            DescriptionHashOnly = true
        };
        var hex = BlinkLnAddressLightningClient.ComputeDescriptionHashHex(req);
        Assert.Equal(Sha256Hex(metadata), hex);
    }

    [Fact]
    public void Explicit_description_hash_is_preferred()
    {
        var dh = new uint256(Sha256Hex("anything"));
        var req = new CreateInvoiceParams(LightMoney.Satoshis(21), "desc", TimeSpan.FromMinutes(15))
        {
            DescriptionHash = dh
        };
        var hex = BlinkLnAddressLightningClient.ComputeDescriptionHashHex(req);
        Assert.Equal(dh.ToString(), hex);
    }

    [Fact]
    public void No_hash_when_not_description_hash_only()
    {
        // A plain memo (no DescriptionHashOnly, no DescriptionHash) => memo is used, not a hash.
        var req = new CreateInvoiceParams(LightMoney.Satoshis(21), "a human memo", TimeSpan.FromMinutes(15));
        Assert.Null(BlinkLnAddressLightningClient.ComputeDescriptionHashHex(req));
    }

    // --- ParseAccountInfo: JSON-null data must NOT throw (regression guard) -------------------

    [Fact]
    public void ParseAccountInfo_custodial_resolves_wallet()
    {
        var resp = JObject.Parse(
            "{\"data\":{\"accountDefaultWallet\":{\"id\":\"0515bb4d-9064-46ba-885a-306e3324c547\",\"walletCurrency\":\"BTC\"}}}");
        var (info, confident) = BlinkGraphQLPublicClient.ParseAccountInfo(resp);
        Assert.Equal(BlinkGraphQLPublicClient.AccountKind.Custodial, info.Kind);
        Assert.Equal("0515bb4d-9064-46ba-885a-306e3324c547", info.WalletId);
        Assert.Equal("BTC", info.WalletCurrency);
        Assert.True(confident); // a resolved wallet is safe to cache
    }

    [Fact]
    public void ParseAccountInfo_noncustodial_does_not_exist_is_confident()
    {
        // This is the exact shape Blink returns for a Spark username - previously threw
        // "Cannot access child value on JValue" and broke metadata mirroring for Spark addresses.
        var resp = JObject.Parse(
            "{\"data\":null,\"errors\":[{\"message\":\"Account does not exist for username twentyone\"}]}");
        var (info, confident) = BlinkGraphQLPublicClient.ParseAccountInfo(resp);
        Assert.Equal(BlinkGraphQLPublicClient.AccountKind.NonCustodial, info.Kind);
        Assert.Null(info.WalletId);
        Assert.True(confident); // an explicit "does not exist" is a confident Spark verdict
    }

    [Fact]
    public void ParseAccountInfo_ambiguous_error_is_not_confident()
    {
        // A transient/ambiguous data:null error (rate-limit, timeout surfaced as 200, server error)
        // must NOT be cached as NonCustodial - otherwise one blip pins a valid custodial account to
        // the LNURL fallback for the whole plugin lifetime.
        foreach (var message in new[] { "internal server error", "rate limited", "Something went wrong" })
        {
            var resp = JObject.Parse(
                "{\"data\":null,\"errors\":[{\"message\":\"" + message + "\"}]}");
            var (info, confident) = BlinkGraphQLPublicClient.ParseAccountInfo(resp);
            Assert.Equal(BlinkGraphQLPublicClient.AccountKind.NonCustodial, info.Kind); // safe fallback for this call
            Assert.False(confident); // but not cached
        }
    }

    // --- ParseStatus: null-safe -------------------------------------------------------------

    [Fact]
    public void ParseStatus_reads_status()
    {
        var resp = JObject.Parse(
            "{\"data\":{\"lnInvoicePaymentStatusByHash\":{\"status\":\"PAID\"}}}");
        Assert.Equal("PAID", BlinkGraphQLPublicClient.ParseStatus(resp));
    }

    [Fact]
    public void ParseStatus_unknown_invoice_data_null_returns_null()
    {
        var resp = JObject.Parse("{\"data\":null,\"errors\":[{\"message\":\"not found\"}]}");
        Assert.Null(BlinkGraphQLPublicClient.ParseStatus(resp));
    }

    // --- ParseInvoiceResult: success + error surfacing --------------------------------------

    [Fact]
    public void ParseInvoiceResult_success()
    {
        var resp = JObject.Parse(
            "{\"data\":{\"lnInvoiceCreateOnBehalfOfRecipient\":{\"invoice\":{\"paymentHash\":\"ab\",\"paymentRequest\":\"lnbc1xyz\"},\"errors\":[]}}}");
        var (pr, ph) = BlinkGraphQLPublicClient.ParseInvoiceResult(resp, "lnInvoiceCreateOnBehalfOfRecipient");
        Assert.Equal("lnbc1xyz", pr);
        Assert.Equal("ab", ph);
    }

    [Fact]
    public void ParseInvoiceResult_field_error_surfaces_message()
    {
        var resp = JObject.Parse(
            "{\"data\":{\"lnInvoiceCreateOnBehalfOfRecipient\":{\"invoice\":null,\"errors\":[{\"message\":\"amount too small\"}]}}}");
        var ex = Assert.Throws<Exception>(() =>
            BlinkGraphQLPublicClient.ParseInvoiceResult(resp, "lnInvoiceCreateOnBehalfOfRecipient"));
        Assert.Equal("amount too small", ex.Message);
    }

    [Fact]
    public void ParseInvoiceResult_toplevel_error_data_null_surfaces_message()
    {
        var resp = JObject.Parse(
            "{\"data\":null,\"errors\":[{\"message\":\"Something went wrong\"}]}");
        var ex = Assert.Throws<Exception>(() =>
            BlinkGraphQLPublicClient.ParseInvoiceResult(resp, "lnInvoiceCreateOnBehalfOfRecipient"));
        Assert.Equal("Something went wrong", ex.Message);
    }
}
