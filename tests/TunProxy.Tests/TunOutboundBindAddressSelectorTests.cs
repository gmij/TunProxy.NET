using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Configuration;
using TunProxy.Core.Route;

namespace TunProxy.Tests;

public class TunOutboundBindAddressSelectorTests
{
    [Fact]
    public void Select_ReturnsNullForLoopbackProxyWithoutProbing()
    {
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("loopback proxy should not be probed"),
            _ => throw new InvalidOperationException("loopback proxy should not inspect gateways"));

        var result = selector.Select(
            new ProxyConfig { Host = "127.0.0.1", Port = 7890 },
            new FakeRouteService("192.168.1.1"));

        Assert.Null(result);
    }

    [Fact]
    public void Select_UsesNonLoopbackProbedAddressFirst()
    {
        var probed = IPAddress.Parse("192.0.2.10");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => probed,
            _ => throw new InvalidOperationException("gateway lookup should not run when probe succeeds"),
            _ => null,
            _ => []);

        var result = selector.Select(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            new FakeRouteService("192.168.1.1"));

        Assert.Equal(probed, result);
    }

    [Fact]
    public void Select_UsesRouteLocalAddressBeforeProbe()
    {
        var routeAddress = IPAddress.Parse("10.144.20.231");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("probe should not run when route lookup succeeds"),
            _ => throw new InvalidOperationException("gateway lookup should not run when route lookup succeeds"));

        var result = selector.Select(
            new ProxyConfig { Host = "10.144.20.222", Port = 7890 },
            new FakeRouteService("192.168.1.1", routeAddress));

        Assert.Equal(routeAddress, result);
    }

    [Fact]
    public void Select_ResolvesHostForRouteLocalAddress()
    {
        var routeAddress = IPAddress.Parse("10.144.20.231");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("probe should not run when route lookup succeeds"),
            _ => throw new InvalidOperationException("gateway lookup should not run when route lookup succeeds"),
            _ => null,
            host => host == "proxy.example" ? [IPAddress.Parse("10.144.20.222")] : []);

        var result = selector.Select(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            new FakeRouteService("192.168.1.1", routeAddress));

        Assert.Equal(routeAddress, result);
    }

    [Fact]
    public void Select_UsesPreferredAddressWhenItMatchesProxyRoute()
    {
        var routeAddress = IPAddress.Parse("10.144.20.231");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("probe should not run when preferred address is usable"),
            _ => throw new InvalidOperationException("gateway lookup should not run when preferred address is usable"),
            _ => null,
            _ => [],
            address => address.Equals(routeAddress));

        var result = selector.Select(
            new ProxyConfig { Host = "10.144.20.222", Port = 7890 },
            new FakeRouteService("192.168.1.1", routeAddress),
            routeAddress);

        Assert.Equal(routeAddress, result);
    }

    [Fact]
    public void Select_IgnoresPreferredAddressWhenProxyRouteChanged()
    {
        var staleAddress = IPAddress.Parse("192.168.66.76");
        var routeAddress = IPAddress.Parse("10.144.20.231");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("probe should not run when route lookup succeeds"),
            _ => throw new InvalidOperationException("gateway lookup should not run when route lookup succeeds"),
            _ => null,
            _ => [],
            address => address.Equals(staleAddress));

        var result = selector.Select(
            new ProxyConfig { Host = "10.144.20.222", Port = 7890 },
            new FakeRouteService("192.168.1.1", routeAddress),
            staleAddress);

        Assert.Equal(routeAddress, result);
    }

    [Fact]
    public void Select_FallsBackToGatewayAddressWhenProbeIsLoopback()
    {
        var gatewayAddress = IPAddress.Parse("192.0.2.20");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => IPAddress.Loopback,
            gateway => gateway == "192.168.1.1" ? gatewayAddress : null,
            _ => null,
            _ => []);

        var result = selector.Select(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            new FakeRouteService("192.168.1.1"));

        Assert.Equal(gatewayAddress, result);
    }

    [Fact]
    public void Select_ReturnsNullWhenNoStableAddressIsFound()
    {
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => null,
            _ => null,
            _ => null,
            _ => []);

        var result = selector.Select(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            new FakeRouteService(null));

        Assert.Null(result);
    }

    [Fact]
    public void Select_PrefersLocalSubnetMatchOverConflictingRoute()
    {
        var subnetAddress = IPAddress.Parse("10.144.20.231");
        var routeAddress = IPAddress.Parse("192.168.66.76");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("probe should not run when subnet match succeeds"),
            _ => throw new InvalidOperationException("gateway lookup should not run when subnet match succeeds"),
            destination => destination.Equals(IPAddress.Parse("10.144.20.222")) ? subnetAddress : null);

        var result = selector.Select(
            new ProxyConfig { Host = "10.144.20.222", Port = 7890 },
            new FakeRouteService("192.168.1.1", routeAddress));

        Assert.Equal(subnetAddress, result);
    }

    [Fact]
    public void Select_UsesPreferredAddressWhenNoCurrentRouteEvidenceExists()
    {
        var preferred = IPAddress.Parse("10.144.20.231");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => throw new InvalidOperationException("probe should not run when preferred address is usable"),
            _ => throw new InvalidOperationException("gateway lookup should not run when preferred address is usable"),
            _ => null,
            _ => [],
            address => address.Equals(preferred));

        var result = selector.Select(
            new ProxyConfig { Host = "10.144.20.222", Port = 7890 },
            new FakeRouteService(null),
            preferred);

        Assert.Equal(preferred, result);
    }

    private sealed class FakeRouteService(string? gateway, IPAddress? localAddress = null) : IRouteService
    {
        public bool AddBypassRoute(string ip, int prefixLength = 32) => true;

        public bool RemoveBypassRoute(string ip) => true;

        public bool AddDefaultRoute() => true;

        public bool RemoveDefaultRoute() => true;

        public string? GetOriginalDefaultGateway() => gateway;

        public IPAddress? GetLocalAddressForDestination(IPAddress destination) => localAddress;

        public void ClearAllBypassRoutes()
        {
        }
    }
}
