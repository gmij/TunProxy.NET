using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class PacGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsLocalProxyEndpointInsteadOfUpstreamProxy()
    {
        var config = new AppConfig
        {
            Proxy = new ProxyConfig
            {
                Host = "upstream.example",
                Port = 7890,
                Type = "Socks5"
            },
            LocalProxy = new LocalProxyConfig
            {
                ListenPort = 18080,
                SystemProxyMode = SystemProxyModes.Pac
            },
            Route = new RouteConfig
            {
                EnableGfwList = false,
                ProxyDomains = ["google.com"]
            }
        };

        var pac = await PacGenerator.GenerateAsync(config);

        Assert.Contains("PROXY 127.0.0.1:18080", pac);
        Assert.DoesNotContain("upstream.example:7890", pac);
        Assert.Contains("\"google.com\"", pac);
    }

    [Fact]
    public async Task GenerateAsync_SmartModeDefaultsToLocalProxyWhenNoRulesAreLoaded()
    {
        var config = new AppConfig
        {
            LocalProxy = new LocalProxyConfig
            {
                ListenPort = 18080,
                SystemProxyMode = SystemProxyModes.Pac
            },
            Route = new RouteConfig
            {
                Mode = "smart",
                EnableGfwList = false,
                EnableGeo = false
            }
        };

        var pac = await PacGenerator.GenerateAsync(config);

        Assert.Contains("return P;", pac);
    }

    [Fact]
    public async Task GenerateAsync_WhenNotPacMode_ReturnsDirectOnlyPac()
    {
        var config = new AppConfig
        {
            LocalProxy = new LocalProxyConfig
            {
                ListenPort = 18080,
                SystemProxyMode = SystemProxyModes.Global
            },
            Route = new RouteConfig
            {
                EnableGfwList = false,
                ProxyDomains = ["google.com"]
            }
        };

        var pac = await PacGenerator.GenerateAsync(config);

        Assert.Contains("TunProxy PAC - disabled", pac);
        Assert.Contains("return \"DIRECT\";", pac);
        Assert.DoesNotContain("PROXY 127.0.0.1:18080", pac);
        Assert.DoesNotContain("\"google.com\"", pac);
    }
}
