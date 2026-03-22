using TunProxy.Proxy;

namespace TunProxy.Tests;

public class Socks5ClientTests
{
    [Fact]
    public void Constructor_ValidParameters_CreatesClient()
    {
        var client = new Socks5Client("127.0.0.1", 7890);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_WithAuth_ValidParameters_CreatesClient()
    {
        var client = new Socks5Client("127.0.0.1", 7890, "user", "pass");
        Assert.NotNull(client);
    }

    [Fact]
    public async Task ConnectAsync_InvalidProxy_ThrowsException()
    {
        var client = new Socks5Client("127.0.0.1", 9999); // 假设 9999 端口没有代理服务

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client.ConnectAsync("example.com", 80);
        });
    }
}

public class HttpProxyClientTests
{
    [Fact]
    public void Constructor_ValidParameters_CreatesClient()
    {
        var client = new HttpProxyClient("127.0.0.1", 8080);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_WithAuth_ValidParameters_CreatesClient()
    {
        var client = new HttpProxyClient("127.0.0.1", 8080, "user", "pass");
        Assert.NotNull(client);
    }

    [Fact]
    public async Task ConnectAsync_InvalidProxy_ThrowsException()
    {
        var client = new HttpProxyClient("127.0.0.1", 9999);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client.ConnectAsync("example.com", 80);
        });
    }
}
