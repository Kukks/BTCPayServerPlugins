using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.LNURLVerify.Tests;

public class EgressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    [InlineData("255.255.255.255")]
    public void Refuses_every_private_class(string ip) => Assert.True(EgressGuard.IsBlocked(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.7")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("192.167.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:db8::1")]
    public void Allows_public_addresses(string ip) => Assert.False(EgressGuard.IsBlocked(IPAddress.Parse(ip)));

    [Fact]
    public void Redirects_are_not_followed() => Assert.False(EgressGuard.Handler(allowPrivate: false).AllowAutoRedirect);

    [Fact]
    public async Task Refuses_a_loopback_literal_without_echoing_internals()
    {
        using var client = new HttpClient(EgressGuard.Handler(allowPrivate: false));

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/x"));

        Assert.Contains("private network address", e.Message);
        Assert.DoesNotContain("127.0.0.1:9/x", e.Message);
    }

    // localhost is only known to be loopback after DNS resolution, which is where a rebinding attack lands.
    [Fact]
    public async Task Refuses_a_host_that_resolves_to_loopback()
    {
        using var client = new HttpClient(EgressGuard.Handler(allowPrivate: false));

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:9/x"));

        Assert.Contains("private network address", e.Message);
        Assert.DoesNotContain("127.0.0.1", e.Message);
    }

    [Fact]
    public async Task The_operator_flag_allows_a_private_destination()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Serve(listener);
        using var client = new HttpClient(EgressGuard.Handler(allowPrivate: true));

        var body = await client.GetStringAsync($"http://127.0.0.1:{port}/x");

        Assert.Equal("ok", body);
        await served;
        listener.Stop();
    }

    // The guard is worthless if the plugin's registration misses a name, so drive the real Execute.
    [Theory]
    [MemberData(nameof(GuardedClientNames))]
    public async Task Every_named_client_the_plugin_registers_is_guarded(string name)
    {
        var services = new ServiceCollection();
        new LNURLVerifyPlugin().Execute(services);
        using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/x"));
        Assert.Contains("private network address", e.Message);
    }

    public static TheoryData<string> GuardedClientNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in EgressGuard.ClientNames) data.Add(name);
        return data;
    }

    static async Task Serve(TcpListener listener)
    {
        using var socket = await listener.AcceptSocketAsync();
        var buffer = new byte[1024];
        await socket.ReceiveAsync(buffer);
        await socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"));
    }
}
