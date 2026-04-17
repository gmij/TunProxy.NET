using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class AppConfigTests
{
    [Fact]
    public void ApplyFrom_CopiesAllNestedConfiguration()
    {
        var target = new AppConfig
        {
            Proxy = new ProxyConfig { Host = "old", Port = 1, Type = "Http" },
            Tun = new TunConfig { Enabled = false, IpAddress = "10.0.0.1", SubnetMask = "255.255.255.0", DnsServer = "1.1.1.1" },
            LocalProxy = new LocalProxyConfig { ListenPort = 8080, SetSystemProxy = false, BypassList = "old" },
            Route = new RouteConfig { Mode = "whitelist", ProxyDomains = ["old.example"], GeoProxy = ["US"] },
            Logging = new LoggingConfig { MinimumLevel = "Information", FilePath = "old.log" }
        };

        var incoming = new AppConfig
        {
            Proxy = new ProxyConfig { Host = "127.0.0.1", Port = 7890, Type = "Socks5", Username = "user", Password = "pass" },
            Tun = new TunConfig
            {
                Enabled = true,
                IpAddress = "10.8.0.1",
                SubnetMask = "255.255.0.0",
                AddDefaultRoute = false,
                DnsServer = "8.8.8.8"
            },
            LocalProxy = new LocalProxyConfig { ListenPort = 9090, SetSystemProxy = true, BypassList = "<local>" },
            Route = new RouteConfig
            {
                Mode = "blacklist",
                ProxyDomains = ["proxy.example"],
                DirectDomains = ["direct.example"],
                EnableGeo = true,
                GeoProxy = ["JP"],
                GeoDirect = ["CN"],
                GeoIpDbPath = "geo.mmdb",
                EnableGfwList = true,
                GfwListUrl = "https://example/gfw.txt",
                GfwListPath = "gfw.txt",
                TunRouteMode = "selective",
                TunRouteApps = ["chrome.exe"],
                AutoAddDefaultRoute = false
            },
            Logging = new LoggingConfig { MinimumLevel = "Debug", FilePath = "new.log" }
        };

        target.ApplyFrom(incoming);

        Assert.Equal("127.0.0.1", target.Proxy.Host);
        Assert.Equal(7890, target.Proxy.Port);
        Assert.Equal("Socks5", target.Proxy.Type);
        Assert.Equal("user", target.Proxy.Username);
        Assert.Equal("pass", target.Proxy.Password);
        Assert.True(target.Tun.Enabled);
        Assert.Equal("10.8.0.1", target.Tun.IpAddress);
        Assert.Equal("255.255.0.0", target.Tun.SubnetMask);
        Assert.False(target.Tun.AddDefaultRoute);
        Assert.Equal("8.8.8.8", target.Tun.DnsServer);
        Assert.Equal(9090, target.LocalProxy.ListenPort);
        Assert.True(target.LocalProxy.SetSystemProxy);
        Assert.Equal("<local>", target.LocalProxy.BypassList);
        Assert.Equal("blacklist", target.Route.Mode);
        Assert.Equal(["proxy.example"], target.Route.ProxyDomains);
        Assert.Equal(["direct.example"], target.Route.DirectDomains);
        Assert.True(target.Route.EnableGeo);
        Assert.Equal(["JP"], target.Route.GeoProxy);
        Assert.Equal(["CN"], target.Route.GeoDirect);
        Assert.Equal("geo.mmdb", target.Route.GeoIpDbPath);
        Assert.True(target.Route.EnableGfwList);
        Assert.Equal("https://example/gfw.txt", target.Route.GfwListUrl);
        Assert.Equal("gfw.txt", target.Route.GfwListPath);
        Assert.Equal("selective", target.Route.TunRouteMode);
        Assert.Equal(["chrome.exe"], target.Route.TunRouteApps);
        Assert.False(target.Route.AutoAddDefaultRoute);
        Assert.Equal("Debug", target.Logging.MinimumLevel);
        Assert.Equal("new.log", target.Logging.FilePath);
    }

    [Fact]
    public void ApplyFrom_ClonesRouteLists()
    {
        var target = new AppConfig();
        var incoming = new AppConfig
        {
            Route = new RouteConfig
            {
                ProxyDomains = ["proxy.example"],
                DirectDomains = ["direct.example"],
                GeoProxy = ["US"],
                GeoDirect = ["CN"],
                TunRouteApps = ["app.exe"]
            }
        };

        target.ApplyFrom(incoming);
        incoming.Route.ProxyDomains.Add("later.example");
        incoming.Route.DirectDomains.Add("later.direct");
        incoming.Route.GeoProxy.Add("JP");
        incoming.Route.GeoDirect.Add("HK");
        incoming.Route.TunRouteApps.Add("later.exe");

        Assert.Equal(["proxy.example"], target.Route.ProxyDomains);
        Assert.Equal(["direct.example"], target.Route.DirectDomains);
        Assert.Equal(["US"], target.Route.GeoProxy);
        Assert.Equal(["CN"], target.Route.GeoDirect);
        Assert.Equal(["app.exe"], target.Route.TunRouteApps);
    }
}
