using System.Net;
using System.Net.NetworkInformation;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class WindowsRouteServiceTests
{
    [Theory]
    [InlineData("On-link")]
    [InlineData("on-link")]
    [InlineData("0.0.0.0")]
    [InlineData("在链路上")]
    [InlineData("在鏈路上")]
    public void IsOnLinkGateway_RecognizesLocalizedOnLinkValues(string gateway)
    {
        Assert.True(WindowsRouteService.IsOnLinkGateway(gateway));
    }

    [Fact]
    public void IsTunDefaultRoute_AcceptsGatewayRouteToTunIp()
    {
        var route = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = "10.0.0.1",
            Interface = "10.0.0.1",
            Metric = "1"
        };

        Assert.True(WindowsRouteService.IsTunDefaultRoute(route, "10.0.0.1"));
    }

    [Theory]
    [InlineData("On-link")]
    [InlineData("在链路上")]
    public void IsTunDefaultRoute_AcceptsOnLinkRouteOnTunInterface(string gateway)
    {
        var route = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = gateway,
            Interface = "10.0.0.1",
            Metric = "1"
        };

        Assert.True(WindowsRouteService.IsTunDefaultRoute(route, "10.0.0.1"));
    }

    [Fact]
    public void IsTunDefaultRoute_RejectsPhysicalDefaultRoute()
    {
        var route = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = "10.144.20.1",
            Interface = "10.144.20.231",
            Metric = "25"
        };

        Assert.False(WindowsRouteService.IsTunDefaultRoute(route, "10.0.0.1"));
    }

    [Fact]
    public void IsTunDefaultRoute_RejectsDefaultRouteViaTunGatewayOnPhysicalInterface()
    {
        var route = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = "10.0.0.1",
            Interface = "10.0.0.138",
            Metric = "25"
        };

        Assert.False(WindowsRouteService.IsTunDefaultRoute(route, "10.0.0.1"));
    }

    [Fact]
    public void IsSpecificRouteForDestination_MatchesExistingCorporateRoute()
    {
        var route = new RouteEntry
        {
            Network = "10.144.0.0",
            Netmask = "255.255.0.0",
            Gateway = "On-link",
            Interface = "10.144.20.231",
            Metric = "25"
        };

        Assert.True(WindowsRouteService.IsSpecificRouteForDestination(route, "10.144.20.222", "10.0.0.1"));
    }

    [Fact]
    public void IsSpecificRouteForDestination_IgnoresDefaultRoutes()
    {
        var route = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = "192.168.66.1",
            Interface = "192.168.66.76",
            Metric = "25"
        };

        Assert.False(WindowsRouteService.IsSpecificRouteForDestination(route, "10.144.20.222", "10.0.0.1"));
    }

    [Fact]
    public void IsSpecificRouteForDestination_RejectsHostRouteThatLoopsBackIntoTun()
    {
        var route = new RouteEntry
        {
            Network = "203.0.113.10",
            Netmask = "255.255.255.255",
            Gateway = "25.255.255.254",
            Interface = "10.0.0.1",
            Metric = "5"
        };

        Assert.False(WindowsRouteService.IsSpecificRouteForDestination(
            route,
            "203.0.113.10",
            "10.0.0.1"));
    }

    [Fact]
    public void GetPrefixLength_ReturnsMaskBits()
    {
        Assert.Equal(16, WindowsRouteService.GetPrefixLength("255.255.0.0"));
        Assert.Equal(24, WindowsRouteService.GetPrefixLength("255.255.255.0"));
        Assert.Equal(32, WindowsRouteService.GetPrefixLength("255.255.255.255"));
    }

    [Fact]
    public void FindLocalAddressForDestination_PrefersSpecificCorporateRoute()
    {
        var routes = new[]
        {
            new RouteEntry
            {
                Network = "0.0.0.0",
                Netmask = "0.0.0.0",
                Gateway = "192.168.66.1",
                Interface = "192.168.66.76",
                Metric = "25"
            },
            new RouteEntry
            {
                Network = "10.144.0.0",
                Netmask = "255.255.0.0",
                Gateway = "On-link",
                Interface = "10.144.20.231",
                Metric = "25"
            }
        };

        var address = WindowsRouteService.FindLocalAddressForDestination(
            routes,
            IPAddress.Parse("10.144.20.222"),
            "10.0.0.1");

        Assert.Equal(IPAddress.Parse("10.144.20.231"), address);
    }

    [Fact]
    public void FindLocalAddressForDestination_IgnoresTunDefaultRoute()
    {
        var routes = new[]
        {
            new RouteEntry
            {
                Network = "0.0.0.0",
                Netmask = "0.0.0.0",
                Gateway = "On-link",
                Interface = "10.0.0.1",
                Metric = "1"
            },
            new RouteEntry
            {
                Network = "0.0.0.0",
                Netmask = "0.0.0.0",
                Gateway = "192.168.66.1",
                Interface = "192.168.66.76",
                Metric = "25"
            }
        };

        var address = WindowsRouteService.FindLocalAddressForDestination(
            routes,
            IPAddress.Parse("203.0.113.10"),
            "10.0.0.1");

        Assert.Equal(IPAddress.Parse("192.168.66.76"), address);
    }

    [Fact]
    public void SelectBestOnLinkRouteCandidate_MatchesProxyAddressToLocalSubnet()
    {
        var candidates = new[]
        {
            new OnLinkRouteCandidate(
                "Wi-Fi",
                12,
                IPAddress.Parse("192.168.66.76"),
                IPAddress.Parse("255.255.255.0")),
            new OnLinkRouteCandidate(
                "Corp",
                24,
                IPAddress.Parse("10.144.20.231"),
                IPAddress.Parse("255.255.0.0"))
        };

        var candidate = WindowsRouteService.SelectBestOnLinkRouteCandidate(
            candidates,
            IPAddress.Parse("10.144.20.222"),
            "10.0.0.1");

        Assert.NotNull(candidate);
        Assert.Equal("Corp", candidate.InterfaceName);
        Assert.Equal(IPAddress.Parse("10.144.20.231"), candidate.LocalAddress);
    }

    [Fact]
    public void SelectBestOnLinkRouteCandidate_PrefersLongestMatchingPrefix()
    {
        var candidates = new[]
        {
            new OnLinkRouteCandidate(
                "CorpWide",
                24,
                IPAddress.Parse("10.144.1.10"),
                IPAddress.Parse("255.255.0.0")),
            new OnLinkRouteCandidate(
                "CorpLan",
                25,
                IPAddress.Parse("10.144.20.231"),
                IPAddress.Parse("255.255.255.0"))
        };

        var candidate = WindowsRouteService.SelectBestOnLinkRouteCandidate(
            candidates,
            IPAddress.Parse("10.144.20.222"),
            "10.0.0.1");

        Assert.NotNull(candidate);
        Assert.Equal("CorpLan", candidate.InterfaceName);
    }

    [Fact]
    public void ResolveTunInterfaceName_PrefersInterfaceWithTunIpOverEarlierWintunAdapter()
    {
        var candidates = new[]
        {
            new TunInterfaceCandidate(
                "OtherVpn",
                "Wintun Userspace Tunnel",
                true,
                ["172.16.10.2"]),
            new TunInterfaceCandidate(
                "TunProxy",
                "Wintun Userspace Tunnel",
                true,
                ["10.0.0.1"])
        };

        var name = WindowsRouteService.ResolveTunInterfaceName(candidates, "10.0.0.1");

        Assert.Equal("TunProxy", name);
    }

    [Fact]
    public void ResolveTunInterfaceName_PrefersTunIpWhenTunProxyNameIsStale()
    {
        var candidates = new[]
        {
            new TunInterfaceCandidate(
                "TunProxy",
                "Wintun Userspace Tunnel",
                false,
                []),
            new TunInterfaceCandidate(
                "TunProxy 2",
                "Wintun Userspace Tunnel",
                true,
                ["10.0.0.1"])
        };

        var name = WindowsRouteService.ResolveTunInterfaceName(candidates, "10.0.0.1");

        Assert.Equal("TunProxy 2", name);
    }

    [Fact]
    public void SelectDirectEgressRoute_ExcludesZeroTierEvenWhenItStartsFirst()
    {
        var routes = new[]
        {
            DefaultRoute("25.255.255.254", "10.144.20.201", 10),
            DefaultRoute("10.30.96.1", "10.30.96.253", 25)
        };
        var interfaces = new[]
        {
            EgressInterface("ZeroTier One", 42, "10.144.20.201", 0, isOverlay: true),
            EgressInterface("Ethernet", 7, "10.30.96.253", 0, isOverlay: false)
        };

        var selected = WindowsRouteService.SelectDirectEgressRoute(routes, interfaces, "10.0.0.1");

        Assert.NotNull(selected);
        Assert.Equal("10.30.96.1", selected.Gateway);
        Assert.Equal(7, selected.InterfaceIndex);
        Assert.Equal(IPAddress.Parse("10.30.96.253"), selected.LocalAddress);
    }

    [Fact]
    public void SelectDirectEgressRoute_ReturnsNullWhileOnlyZeroTierIsReady()
    {
        var routes = new[] { DefaultRoute("25.255.255.254", "10.144.20.201", 10) };
        var interfaces = new[]
        {
            EgressInterface("ZeroTier One", 42, "10.144.20.201", 0, isOverlay: true)
        };

        var selected = WindowsRouteService.SelectDirectEgressRoute(routes, interfaces, "10.0.0.1");

        Assert.Null(selected);
    }

    [Fact]
    public void SelectDirectEgressRoute_UsesRouteAndInterfaceMetric()
    {
        var routes = new[]
        {
            DefaultRoute("192.168.1.1", "192.168.1.10", 30),
            DefaultRoute("10.30.96.1", "10.30.96.253", 20)
        };
        var interfaces = new[]
        {
            EgressInterface("Wi-Fi", 12, "192.168.1.10", 5, isOverlay: false),
            EgressInterface("Ethernet", 7, "10.30.96.253", 50, isOverlay: false)
        };

        var selected = WindowsRouteService.SelectDirectEgressRoute(routes, interfaces, "10.0.0.1");

        Assert.NotNull(selected);
        Assert.Equal("192.168.1.1", selected.Gateway);
        Assert.Equal(35, selected.TotalMetric);
    }

    [Theory]
    [InlineData("ZeroTier One", "ZeroTier Virtual Port", NetworkInterfaceType.Ethernet, true)]
    [InlineData("Tailscale", "WireGuard Tunnel", NetworkInterfaceType.Tunnel, true)]
    [InlineData("Ethernet", "Intel(R) Ethernet Controller", NetworkInterfaceType.Ethernet, false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi", NetworkInterfaceType.Wireless80211, false)]
    public void IsOverlayInterface_ClassifiesDirectEgressCandidates(
        string name,
        string description,
        NetworkInterfaceType interfaceType,
        bool expected)
    {
        Assert.Equal(expected, WindowsRouteService.IsOverlayInterface(name, description, interfaceType));
    }

    [Fact]
    public void SelectBestOnLinkRouteCandidate_CanExcludeOverlayForDirectTraffic()
    {
        var candidates = new[]
        {
            new OnLinkRouteCandidate(
                "ZeroTier One",
                42,
                IPAddress.Parse("10.144.20.201"),
                IPAddress.Parse("255.255.0.0"),
                IsOverlay: true)
        };

        var direct = WindowsRouteService.SelectBestOnLinkRouteCandidate(
            candidates,
            IPAddress.Parse("10.144.20.200"),
            "10.0.0.1",
            allowOverlay: false);
        var proxy = WindowsRouteService.SelectBestOnLinkRouteCandidate(
            candidates,
            IPAddress.Parse("10.144.20.200"),
            "10.0.0.1",
            allowOverlay: true);

        Assert.Null(direct);
        Assert.NotNull(proxy);
        Assert.Equal("ZeroTier One", proxy.InterfaceName);
    }

    private static RouteEntry DefaultRoute(string gateway, string localAddress, int metric) => new()
    {
        Network = "0.0.0.0",
        Netmask = "0.0.0.0",
        Gateway = gateway,
        Interface = localAddress,
        Metric = metric.ToString()
    };

    private static DirectEgressInterfaceCandidate EgressInterface(
        string name,
        int index,
        string localAddress,
        int metric,
        bool isOverlay) =>
        new(
            name,
            name,
            index,
            IPAddress.Parse(localAddress),
            metric,
            isOverlay,
            []);
}
