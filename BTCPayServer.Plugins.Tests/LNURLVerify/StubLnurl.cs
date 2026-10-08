using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Plugins.LNURLVerify.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Tests.LNURLVerify;

/// <summary>
/// An LNURL offering USDT on Arbitrum Sepolia, Solana devnet and Tron Nile at 63,360 USDT/BTC, and Lightning with self-signed
/// regtest invoices that never settle. A token destination settles once the test calls <see cref="Settle"/> with its verify id.
/// <see cref="FixedFloatPayUrl"/> answers as lnurl-server's FixedFloat rails do: each callback is a new order with its own
/// deposit address and verify id (<c>option/n</c>), and verify reports the payer's deposit transaction.
/// </summary>
public sealed class StubLnurl : IAsyncDisposable
{
    public const string ArbitrumToken = "0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB";
    public const string EvmRecipient = "0x1111111111111111111111111111111111111111";
    public const string SolanaMint = "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";
    public const string SolanaRecipient = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";
    public const string TronToken = "TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf";
    public const string TronRecipient = "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7";
    public const string ArbitrumUsdt = "0xFd086bC7CD5C481DCC9C85ebE478A1C0b69FCbb9";

    private static readonly Dictionary<string, (string Asset, string Unit, long MinSendable)> FixedFloatRails = new()
    {
        ["ff-usdtarbitrum"] = ("eip155:42161/erc20:" + ArbitrumUsdt, "USDT", 2_844_000),
        ["ff-usdttrc"] = ("tron:0x2b6653dc/trc20:TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", "USDT", 11_996_000),
        ["ff-usdcarbitrum"] = ("eip155:42161/erc20:0xaf88d065e77c8cC2239327C5EDb3A432268e5831", "USDC", 2_863_000)
    };

    private readonly IHost _host;
    private readonly Key _node = new();
    private readonly ConcurrentDictionary<string, string> _settled = new();
    private readonly ConcurrentDictionary<string, int> _orders = new();
    private readonly ConcurrentDictionary<string, (string Option, string Deposit)> _deposits = new();
    private int _batchVerifies, _fixedFloatSingleVerifies;

    private StubLnurl(IHost host, Uri root)
    {
        _host = host;
        Root = root;
    }

    public Uri Root { get; }
    public string PayUrl => new Uri(Root, ".well-known/lnurlp/merchant").AbsoluteUri;
    public string FixedFloatPayUrl => new Uri(Root, ".well-known/lnurlp/fixedfloat").AbsoluteUri;
    /// <summary>Per-option FixedFloat quote lifetimes, read as each order is made; ten minutes for an option with none.</summary>
    public ConcurrentDictionary<string, TimeSpan> QuoteLifetimes { get; } = new();
    public int BatchVerifies => _batchVerifies;
    public int FixedFloatSingleVerifies => _fixedFloatSingleVerifies;
    public int Orders(string option) => _orders.GetValueOrDefault(option);

    public static string DepositAddress(string option, int n)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes($"{option}/{n}"))[..20];
        return option.EndsWith("trc") ? Encoders.Base58Check.EncodeData(new byte[] { 0x41 }.Concat(key).ToArray()) : "0x" + Convert.ToHexString(key).ToLowerInvariant();
    }

    public static async Task<StubLnurl> Start()
    {
        StubLnurl? stub = null;
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHostDefaults(web => web.UseKestrel().UseUrls("http://127.0.0.1:0")
                .Configure(app => app.Run(ctx => stub!.Handle(ctx))))
            .Build();
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return stub = new StubLnurl(host, new Uri(address + "/"));
    }

    public void Settle(string verifyId, string reference) => _settled[verifyId] = reference;

    private async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var body = path switch
        {
            "/.well-known/lnurlp/merchant" => PayRequest(false),
            "/.well-known/lnurlp/fixedfloat" => PayRequest(true),
            "/cb" => Callback(ctx.Request.Query["paymentOption"].ToString(), long.Parse(ctx.Request.Query["amount"].ToString())),
            "/verifyBatch" => VerifyBatch(ctx.Request.Query["verify"]),
            _ when path.StartsWith("/verify/") => Verify(path["/verify/".Length..], single: true),
            _ => new JObject { ["status"] = "ERROR", ["reason"] = "not found" }
        };
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body.ToString());
    }

    private JObject PayRequest(bool fixedFloat) => new()
    {
        ["tag"] = "payRequest", ["callback"] = new Uri(Root, "cb").AbsoluteUri, ["minSendable"] = 1000, ["maxSendable"] = 100_000_000_000,
        ["metadata"] = "[[\"text/plain\",\"stub merchant\"]]",
        ["units"] = fixedFloat
            ? new JArray(new JObject { ["code"] = "USDT", ["decimals"] = 6, ["name"] = "Tether USD" }, new JObject { ["code"] = "USDC", ["decimals"] = 6, ["name"] = "USD Coin" })
            : new JArray(new JObject { ["code"] = "USDT", ["decimals"] = 6, ["name"] = "Tether USD" }),
        ["paymentOptions"] = fixedFloat
            ? new JArray(FixedFloatRails.Select(r => (object)new JObject
            {
                ["id"] = r.Key, ["type"] = r.Value.Asset.Split(':')[0], ["asset"] = r.Value.Asset, ["unit"] = r.Value.Unit, ["provider"] = "FixedFloat",
                ["verifiable"] = true, ["minSendable"] = r.Value.MinSendable, ["maxSendable"] = 100_000_000
            }).Prepend(Lightning()).ToArray())
            : new JArray(Lightning(),
                Token("usdt-arbitrum", "eip155:421614/erc20:" + ArbitrumToken),
                Token("usdt-solana", "solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1/token:" + SolanaMint),
                Token("usdt-tron", "tron:0xcd8690dc/trc20:" + TronToken))
    };

    private static JObject Lightning() => new() { ["id"] = "lightning", ["type"] = "lightning", ["verifiable"] = true };

    private static JObject Token(string id, string asset) =>
        new() { ["id"] = id, ["type"] = asset.Split(':')[0], ["asset"] = asset, ["unit"] = "USDT", ["verifiable"] = true };

    private static JObject Msat(long msat) => new() { ["amount"] = msat.ToString(), ["unit"] = "msat" };

    // No top-level expiresAt: the deposit address dies with the quote, and the plugin refuses a destination that expires before the invoice.
    private JObject FixedFloatOrder(string option, long msat)
    {
        var n = _orders.AddOrUpdate(option, 1, (_, c) => c + 1);
        var deposit = DepositAddress(option, n);
        _deposits[$"{option}/{n}"] = (option, deposit);
        var rail = FixedFloatRails[option];
        var baseUnits = (new BigInteger(msat) * 63_360 / 100_000).ToString();
        var answer = new JObject
        {
            ["status"] = "OK", ["paymentOption"] = option, ["paymentDestination"] = deposit,
            ["paymentQuote"] = new JObject
            {
                ["id"] = $"FF{n:D4}",
                ["expiresAt"] = DateTimeOffset.UtcNow.Add(QuoteLifetimes.TryGetValue(option, out var lifetime) ? lifetime : TimeSpan.FromMinutes(10)).ToUnixTimeSeconds(),
                ["requested"] = Msat(msat), ["payment"] = new JObject { ["amount"] = baseUnits, ["unit"] = rail.Unit }, ["receive"] = Msat(msat - 20_000),
                ["fees"] = new JArray(new JObject { ["name"] = "provider", ["amount"] = Msat(230_000) }, new JObject { ["name"] = "solver", ["amount"] = Msat(20_000) })
            },
            ["verify"] = new Uri(Root, $"verify/{option}/{n}").AbsoluteUri, ["verifyBatch"] = new Uri(Root, "verifyBatch").AbsoluteUri
        };
        if (rail.Asset.StartsWith("eip155:"))
            answer["paymentURI"] = $"ethereum:{rail.Asset.Split(':')[^1]}@42161/transfer?address={deposit}&uint256={baseUnits}";
        return answer;
    }

    private JObject Callback(string option, long msat)
    {
        if (option.StartsWith("ff-")) return FixedFloatOrder(option, msat);
        if (option is "" or "lightning")
        {
            var preimage = RandomNumberGenerator.GetBytes(32);
            return new JObject
            {
                ["pr"] = TestBolt11.Create(_node, msat, SHA256.HashData(preimage)), ["routes"] = new JArray(),
                ["verify"] = new Uri(Root, "verify/ln-" + Convert.ToHexString(preimage)).AbsoluteUri
            };
        }
        var recipient = option switch { "usdt-arbitrum" => EvmRecipient, "usdt-solana" => SolanaRecipient, _ => TronRecipient };
        return new JObject
        {
            ["paymentOption"] = option, ["paymentDestination"] = recipient,
            ["paymentQuote"] = new JObject
            {
                ["requested"] = new JObject { ["amount"] = msat.ToString(), ["unit"] = "msat" },
                ["payment"] = new JObject { ["amount"] = (new BigInteger(msat) * 63_360 / 100_000).ToString(), ["unit"] = "USDT" },
                ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
            },
            ["verify"] = new Uri(Root, "verify/" + option).AbsoluteUri,
            ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()
        };
    }

    private JObject Verify(string id, bool single = false)
    {
        var json = new JObject { ["status"] = "OK", ["settled"] = _settled.TryGetValue(id, out var reference) };
        if (reference is not null) json["paymentReference"] = reference;
        if (_deposits.TryGetValue(id, out var order))
        {
            if (single) Interlocked.Increment(ref _fixedFloatSingleVerifies);
            json["paymentOption"] = order.Option;
            json["paymentDestination"] = order.Deposit;
            json["verifyBatch"] = new Uri(Root, "verifyBatch").AbsoluteUri;
        }
        return json;
    }

    private JObject VerifyBatch(IEnumerable<string?> verifyUrls)
    {
        Interlocked.Increment(ref _batchVerifies);
        return new JObject
        {
            ["status"] = "OK",
            ["results"] = new JObject(verifyUrls.OfType<string>().Distinct()
                .Select(u => new JProperty(u, Verify(u[(u.IndexOf("/verify/", StringComparison.Ordinal) + "/verify/".Length)..]))))
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
