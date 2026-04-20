using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Configuration;

namespace TunProxy.Tests;

public class TunProxyRouteDecisionTests
{
    [Fact]
    public async Task GfwListHit_AlwaysUsesProxy()
    {
        var config = new AppConfig
        {
            Route =
            {
                EnableGfwList = true,
                EnableGeo = true,
                DirectDomains = ["example.com"]
            }
        };
        var service = new RouteDecisionService(
            config,
            isInGfwList: domain => domain == "example.com",
            getCountryCode: _ => "CN",
            resolveHost: (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("1.1.1.1")));

        var decision = await service.DecideForDomainAsync("example.com", CancellationToken.None);

        Assert.True(decision.ShouldProxy);
        Assert.Equal("GFW", decision.Reason);
    }

    [Fact]
    public async Task ProxyDomainWinsOverDirectDomain()
    {
        var config = new AppConfig
        {
            Route =
            {
                ProxyDomains = ["example.com"],
                DirectDomains = ["example.com"]
            }
        };
        var service = new RouteDecisionService(config);

        var decision = await service.DecideForDomainAsync("www.example.com", CancellationToken.None);

        Assert.True(decision.ShouldProxy);
        Assert.Equal("ProxyDomain", decision.Reason);
    }

    [Fact]
    public async Task PrivateIp_UsesDirectRoute()
    {
        var service = new RouteDecisionService(new AppConfig { Route = { EnableGeo = true } });

        var decision = await service.DecideForTunAsync(null, IPAddress.Parse("192.168.66.1"), CancellationToken.None);

        Assert.False(decision.ShouldProxy);
        Assert.Equal("PrivateIP", decision.Reason);
    }

    [Fact]
    public async Task DomainGeoResolution_CnDefaultsToDirect()
    {
        var config = new AppConfig { Route = { EnableGeo = true } };
        var service = new RouteDecisionService(
            config,
            getCountryCode: _ => "CN",
            resolveHost: (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("203.0.113.1")));

        var decision = await service.DecideForDomainAsync("cdn.example.cn", CancellationToken.None);

        Assert.False(decision.ShouldProxy);
        Assert.Equal("Geo:CN", decision.Reason);
    }

    [Fact]
    public void GeoDefault_UsesCnDirectAndUnknownProxy()
    {
        Assert.False(RouteDecisionService.ShouldProxyByGeo("CN", [], []));
        Assert.True(RouteDecisionService.ShouldProxyByGeo("US", [], []));
        Assert.True(RouteDecisionService.ShouldProxyByGeo(null, [], []));
    }

    [Fact]
    public async Task TunDomainHint_CanUpgradeCnIpToProxyByGfwList()
    {
        var config = new AppConfig
        {
            Route =
            {
                EnableGfwList = true,
                EnableGeo = true
            }
        };
        var service = new RouteDecisionService(
            config,
            isInGfwList: domain => domain == "blocked.example",
            getCountryCode: _ => "CN");

        var decision = await service.DecideForTunAsync(
            "blocked.example",
            IPAddress.Parse("203.0.113.1"),
            CancellationToken.None);

        Assert.True(decision.ShouldProxy);
        Assert.Equal("GFW", decision.Reason);
    }

    [Fact]
    public async Task TunDomainHint_ResolvedPrivateIpUsesDirectRoute()
    {
        var service = new RouteDecisionService(
            new AppConfig { Route = { EnableGeo = true } },
            resolveHost: (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("192.168.98.246")));

        var decision = await service.DecideForTunAsync(
            "tc.luomi.cn",
            IPAddress.Parse("203.0.113.10"),
            CancellationToken.None);

        Assert.False(decision.ShouldProxy);
        Assert.Equal("ResolvedPrivateIP", decision.Reason);
        Assert.Equal(IPAddress.Parse("192.168.98.246"), decision.EvaluatedIp);
    }

    [Fact]
    public async Task TunDomainHint_FallsBackToResolvedIpWhenPacketIpHasNoGeo()
    {
        var service = new RouteDecisionService(
            new AppConfig { Route = { EnableGeo = true } },
            getCountryCode: ip => ip.Equals(IPAddress.Parse("203.0.113.20")) ? "CN" : null,
            resolveHost: (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("203.0.113.20")));

        var decision = await service.DecideForTunAsync(
            "cdn.example.cn",
            IPAddress.Parse("198.51.100.10"),
            CancellationToken.None);

        Assert.False(decision.ShouldProxy);
        Assert.Equal("Geo:CN", decision.Reason);
        Assert.Equal(IPAddress.Parse("203.0.113.20"), decision.EvaluatedIp);
    }

}
