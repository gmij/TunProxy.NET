using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Route;

namespace TunProxy.Tests;

public class DirectBypassRouteManagerTests
{
    [Fact]
    public async Task CleanupExpired_RemovesOnlyTrackedIdleRoutes()
    {
        var routes = new FakeRouteService();
        var manager = new DirectBypassRouteManager(routes, TimeSpan.FromSeconds(1));
        var decision = RouteDecision.Direct("Geo:CN", null, IPAddress.Parse("203.0.113.10"));

        await manager.EnsureRouteAsync("203.0.113.10", decision, CancellationToken.None);

        manager.CleanupExpired(DateTime.UtcNow.AddSeconds(2));

        Assert.Contains("203.0.113.10", routes.Added);
        Assert.Contains("203.0.113.10", routes.RemovedTracked);
        Assert.Empty(manager.GetSnapshot());
    }

    [Fact]
    public async Task Touch_KeepsRecentRoute()
    {
        var routes = new FakeRouteService();
        var manager = new DirectBypassRouteManager(routes, TimeSpan.FromMinutes(10));
        var decision = RouteDecision.Direct("Geo:CN", null, IPAddress.Parse("203.0.113.10"));

        await manager.EnsureRouteAsync("203.0.113.10", decision, CancellationToken.None);
        manager.Touch("203.0.113.10");
        manager.CleanupExpired(DateTime.UtcNow.AddMinutes(1));

        Assert.Empty(routes.RemovedTracked);
        Assert.Equal(["203.0.113.10"], manager.GetSnapshot());
    }

    private sealed class FakeRouteService : IRouteService
    {
        public List<string> Added { get; } = new();

        public List<string> RemovedTracked { get; } = new();

        public bool AddBypassRoute(string ip, int prefixLength = 32)
        {
            Added.Add(ip);
            return true;
        }

        public bool RemoveBypassRoute(string ip) => true;

        public bool RemoveTrackedBypassRoute(string ip)
        {
            RemovedTracked.Add(ip);
            return true;
        }

        public bool AddDefaultRoute() => true;

        public bool RemoveDefaultRoute() => true;

        public string? GetOriginalDefaultGateway() => "192.168.1.1";

        public void ClearAllBypassRoutes()
        {
        }
    }
}
