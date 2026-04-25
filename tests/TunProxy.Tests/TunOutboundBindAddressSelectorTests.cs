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
            _ => throw new InvalidOperationException("gateway lookup should not run when probe succeeds"));

        var result = selector.Select(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            new FakeRouteService("192.168.1.1"));

        Assert.Equal(probed, result);
    }

    [Fact]
    public void Select_FallsBackToGatewayAddressWhenProbeIsLoopback()
    {
        var gatewayAddress = IPAddress.Parse("192.0.2.20");
        var selector = new TunOutboundBindAddressSelector(
            (_, _) => IPAddress.Loopback,
            gateway => gateway == "192.168.1.1" ? gatewayAddress : null);

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
            _ => null);

        var result = selector.Select(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            new FakeRouteService(null));

        Assert.Null(result);
    }

    private sealed class FakeRouteService(string? gateway) : IRouteService
    {
        public bool AddBypassRoute(string ip, int prefixLength = 32) => true;

        public bool RemoveBypassRoute(string ip) => true;

        public bool AddDefaultRoute() => true;

        public bool RemoveDefaultRoute() => true;

        public string? GetOriginalDefaultGateway() => gateway;

        public void ClearAllBypassRoutes()
        {
        }
    }
}
