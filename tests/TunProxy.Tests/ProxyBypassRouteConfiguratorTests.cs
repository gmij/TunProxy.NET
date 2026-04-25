using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Configuration;
using TunProxy.Core.Route;

namespace TunProxy.Tests;

public class ProxyBypassRouteConfiguratorTests
{
    [Fact]
    public void ResolveProxyAddresses_ReturnsNonLoopbackIpLiteral()
    {
        var configurator = new ProxyBypassRouteConfigurator();

        var addresses = configurator.ResolveProxyAddresses("203.0.113.10");

        Assert.Equal([IPAddress.Parse("203.0.113.10")], addresses);
    }

    [Fact]
    public void ResolveProxyAddresses_ExcludesLoopbackAndIpv6Addresses()
    {
        var configurator = new ProxyBypassRouteConfigurator(_ =>
        [
            IPAddress.Loopback,
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("203.0.113.10")
        ]);

        var addresses = configurator.ResolveProxyAddresses("proxy.example");

        Assert.Equal([IPAddress.Parse("203.0.113.10")], addresses);
    }

    [Fact]
    public void AddProxyBypassRoutes_ReturnsOnlySuccessfullyAddedRoutes()
    {
        var routeService = new FakeRouteService(ip => ip == "203.0.113.10");
        var configurator = new ProxyBypassRouteConfigurator(_ =>
        [
            IPAddress.Parse("203.0.113.10"),
            IPAddress.Parse("203.0.113.11")
        ]);

        var added = configurator.AddProxyBypassRoutes(
            new ProxyConfig { Host = "proxy.example", Port = 7890 },
            routeService);

        Assert.Equal(["203.0.113.10"], added);
        Assert.Equal(["203.0.113.10", "203.0.113.11"], routeService.Attempted);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("203.0.113.10", true)]
    public void IsBypassRouteCandidate_AllowsOnlyNonLoopbackIpv4(string value, bool expected)
    {
        Assert.Equal(expected, ProxyBypassRouteConfigurator.IsBypassRouteCandidate(IPAddress.Parse(value)));
    }

    private sealed class FakeRouteService(Func<string, bool> addResult) : IRouteService
    {
        public List<string> Attempted { get; } = new();

        public bool AddBypassRoute(string ip, int prefixLength = 32)
        {
            Attempted.Add(ip);
            return addResult(ip);
        }

        public bool RemoveBypassRoute(string ip) => true;

        public bool AddDefaultRoute() => true;

        public bool RemoveDefaultRoute() => true;

        public string? GetOriginalDefaultGateway() => null;

        public void ClearAllBypassRoutes()
        {
        }
    }
}
