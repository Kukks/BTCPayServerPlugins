#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace BTCPayServer.Plugins.LNURLVerify;

/// <summary>
/// Refuses outbound requests to destinations that are not publicly routable. The check runs after DNS
/// resolution, at connect time, so it covers a host that rebinds as well as the callback, verify, payLink and
/// withdraw URLs an LNURL service returns — none of which are vetted anywhere else.
/// </summary>
public static class EgressGuard
{
    public const string ConfigKey = "LNURLVERIFY_ALLOWPRIVATE";
    public const string Refusal = "The LNURL host resolves to a private network address";

    /// <summary>Every named client this plugin sends an LNURL request through.</summary>
    public static readonly string[] ClientNames =
    {
        nameof(LNURLVerifyConnectionStringHandler), nameof(LNURLVerifyPollerService), nameof(LnurlRailProvisioner),
        nameof(LnurlRailPaymentHandler), nameof(LnurlTokenPaymentHandler), nameof(LnurlRailSettingsController)
    };

    /// <summary>Operator-only: lets the guard through for a docker-internal lnurl-server or a regtest stub.</summary>
    public static bool AllowPrivate(IConfiguration? configuration) =>
        configuration?[ConfigKey]?.Trim() is { } value &&
        (value == "1" || (bool.TryParse(value, out var allow) && allow));

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast;
        var b = address.GetAddressBytes();
        return b[0] == 0
               || b[0] == 10
               || (b[0] == 172 && (b[1] & 0xf0) == 16)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 100 && (b[1] & 0xc0) == 64)
               || (b[0] == 169 && b[1] == 254)
               // From 224 up: multicast, reserved and the broadcast address are all equally not a public host.
               || b[0] >= 224;
    }

    public static SocketsHttpHandler Handler(bool allowPrivate)
    {
        // Redirects are not followed: a public LNURL answering 302 would otherwise pick the destination itself.
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (!allowPrivate) handler.ConnectCallback = Connect;
        return handler;
    }

    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, ct);
        // Connecting to the vetted address rather than the name is what closes the window between check and connect.
        var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException(Refusal);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
