using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
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
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Tests.LNURLVerify;

/// <summary>
/// An LNURL offering USDT on Arbitrum Sepolia, Solana devnet and Tron Nile at 63,360 USDT/BTC, and Lightning with self-signed
/// regtest invoices that never settle. A token destination settles once the test calls <see cref="Settle"/>.
/// </summary>
public sealed class StubLnurl : IAsyncDisposable
{
    public const string ArbitrumToken = "0x30fA2FbE15c1EaDfbEF28C188b7B8dbd3c1Ff2eB";
    public const string EvmRecipient = "0x1111111111111111111111111111111111111111";
    public const string SolanaMint = "4zMMC9srt5Ri5X14GAgXhaHii3GnPAEERYPJgZJDncDU";
    public const string SolanaRecipient = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";
    public const string TronToken = "TXYZopYRdj2D9XRtbG411XZZ3kM5VkAeBf";
    public const string TronRecipient = "TLa2f6VPqDgRE67v1736s7bJ8Ray5wYjU7";

    private readonly IHost _host;
    private readonly Key _node = new();
    private readonly ConcurrentDictionary<string, string> _settled = new();

    private StubLnurl(IHost host, Uri root)
    {
        _host = host;
        Root = root;
    }

    public Uri Root { get; }
    public string PayUrl => new Uri(Root, ".well-known/lnurlp/merchant").AbsoluteUri;

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

    public void Settle(string optionId, string reference) => _settled[optionId] = reference;

    private async Task Handle(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var body = path switch
        {
            "/.well-known/lnurlp/merchant" => PayRequest(),
            "/cb" => Callback(ctx.Request.Query["paymentOption"].ToString(), long.Parse(ctx.Request.Query["amount"].ToString())),
            _ when path.StartsWith("/verify/") => Verify(path["/verify/".Length..]),
            _ => new JObject { ["status"] = "ERROR", ["reason"] = "not found" }
        };
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body.ToString());
    }

    private JObject PayRequest() => new()
    {
        ["tag"] = "payRequest", ["callback"] = new Uri(Root, "cb").AbsoluteUri, ["minSendable"] = 1000, ["maxSendable"] = 100_000_000_000,
        ["metadata"] = "[[\"text/plain\",\"stub merchant\"]]",
        ["units"] = new JArray(new JObject { ["code"] = "USDT", ["decimals"] = 6, ["name"] = "Tether USD" }),
        ["paymentOptions"] = new JArray(
            new JObject { ["id"] = "lightning", ["type"] = "lightning", ["verifiable"] = true },
            Token("usdt-arbitrum", "eip155:421614/erc20:" + ArbitrumToken),
            Token("usdt-solana", "solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1/token:" + SolanaMint),
            Token("usdt-tron", "tron:0xcd8690dc/trc20:" + TronToken))
    };

    private static JObject Token(string id, string asset) =>
        new() { ["id"] = id, ["type"] = asset.Split(':')[0], ["asset"] = asset, ["unit"] = "USDT", ["verifiable"] = true };

    private JObject Callback(string option, long msat)
    {
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

    private JObject Verify(string id) => _settled.TryGetValue(id, out var reference)
        ? new JObject { ["status"] = "OK", ["settled"] = true, ["paymentReference"] = reference }
        : new JObject { ["status"] = "OK", ["settled"] = false };

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
