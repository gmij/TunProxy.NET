using TunProxy.CLI;

namespace TunProxy.Tests;

public class WindowsSystemProxyConfigTests
{
    [Fact]
    public void ParseProxyServer_ParsesBareEndpointAsHttpProxy()
    {
        var config = WindowsSystemProxyConfig.ParseProxyServer("10.0.0.2:8080");

        Assert.NotNull(config);
        Assert.Equal("10.0.0.2", config.Host);
        Assert.Equal(8080, config.Port);
        Assert.Equal("Http", config.Type);
    }

    [Fact]
    public void ParseProxyServer_PrefersSocksEndpointWhenProtocolMapContainsSocks()
    {
        var config = WindowsSystemProxyConfig.ParseProxyServer("http=127.0.0.1:7890;https=127.0.0.1:7890;socks=127.0.0.1:7891");

        Assert.NotNull(config);
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(7891, config.Port);
        Assert.Equal("Socks5", config.Type);
    }

    [Fact]
    public void ParseProxyServer_UsesHttpEndpointWhenNoSocksEndpointExists()
    {
        var config = WindowsSystemProxyConfig.ParseProxyServer("http=proxy.local:3128;https=proxy.local:3128");

        Assert.NotNull(config);
        Assert.Equal("proxy.local", config.Host);
        Assert.Equal(3128, config.Port);
        Assert.Equal("Http", config.Type);
    }

    [Fact]
    public void ParseProxyServer_ParsesSocksUriAsSocks5()
    {
        var config = WindowsSystemProxyConfig.ParseProxyServer("socks5://127.0.0.1:10808");

        Assert.NotNull(config);
        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(10808, config.Port);
        Assert.Equal("Socks5", config.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http=")]
    public void ParseProxyServer_ReturnsNullForUnusableValues(string value)
    {
        Assert.Null(WindowsSystemProxyConfig.ParseProxyServer(value));
    }
}
