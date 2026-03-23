using System.Net;
using TunProxy.Core.Connections;

namespace TunProxy.Tests;

public class Socks5ClientTests
{
    [Fact]
    public void Constructor_ValidParameters_CreatesClient()
    {
        var conn = new TcpConnection("127.0.0.1", 7890, ProxyType.Socks5);
        Assert.NotNull(conn);
    }

    [Fact]
    public void Constructor_WithAuth_ValidParameters_CreatesClient()
    {
        var conn = new TcpConnection("127.0.0.1", 7890, ProxyType.Socks5, "user", "pass");
        Assert.NotNull(conn);
    }

    [Fact]
    public async Task ConnectAsync_InvalidProxy_ThrowsException()
    {
        var conn = new TcpConnection("127.0.0.1", 9999, ProxyType.Socks5); // 假设 9999 端口没有代理服务

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token;
            await conn.ConnectAsync("example.com", 80, ct);
        });
    }
}

public class HttpProxyClientTests
{
    [Fact]
    public void Constructor_ValidParameters_CreatesClient()
    {
        var conn = new TcpConnection("127.0.0.1", 8080, ProxyType.Http);
        Assert.NotNull(conn);
    }

    [Fact]
    public void Constructor_WithAuth_ValidParameters_CreatesClient()
    {
        var conn = new TcpConnection("127.0.0.1", 8080, ProxyType.Http, "user", "pass");
        Assert.NotNull(conn);
    }

    [Fact]
    public async Task ConnectAsync_InvalidProxy_ThrowsException()
    {
        var conn = new TcpConnection("127.0.0.1", 9999, ProxyType.Http);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token;
            await conn.ConnectAsync("example.com", 80, ct);
        });
    }
}
