using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunRouteDiagnosticsBuilderTests
{
    [Fact]
    public void ApplyWindowsRouteTableDiagnostics_ReportsMissingTunDefaultRoute()
    {
        var snapshot = new RouteDiagnosticsSnapshot();

        TunRouteDiagnosticsBuilder.ApplyWindowsRouteTableDiagnostics(
            snapshot,
            "10.0.0.1",
            [],
            tunDefault: null);

        Assert.False(snapshot.HasTunDefaultRoute);
        Assert.Contains("TUN default route is missing.", snapshot.Issues);
    }

    [Fact]
    public void ApplyWindowsRouteTableDiagnostics_ReportsHigherPriorityCompetingDefaultRoute()
    {
        var snapshot = new RouteDiagnosticsSnapshot();
        var tunDefault = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = "10.0.0.1",
            Interface = "10.0.0.1",
            Metric = "10"
        };
        var routes = new[]
        {
            tunDefault,
            new RouteEntry
            {
                Network = "0.0.0.0",
                Netmask = "0.0.0.0",
                Gateway = "192.168.1.1",
                Interface = "192.168.1.10",
                Metric = "5"
            }
        };

        TunRouteDiagnosticsBuilder.ApplyWindowsRouteTableDiagnostics(
            snapshot,
            "10.0.0.1",
            routes,
            tunDefault);

        Assert.True(snapshot.HasTunDefaultRoute);
        Assert.Equal("10", snapshot.TunDefaultMetric);
        Assert.Contains("Another default route has a higher priority than TUN metric 10.", snapshot.Issues);
    }

    [Fact]
    public void ApplyWindowsRouteTableDiagnostics_IgnoresLowerPriorityCompetingDefaultRoute()
    {
        var snapshot = new RouteDiagnosticsSnapshot();
        var tunDefault = new RouteEntry
        {
            Network = "0.0.0.0",
            Netmask = "0.0.0.0",
            Gateway = "10.0.0.1",
            Interface = "10.0.0.1",
            Metric = "1"
        };
        var routes = new[]
        {
            tunDefault,
            new RouteEntry
            {
                Network = "0.0.0.0",
                Netmask = "0.0.0.0",
                Gateway = "192.168.1.1",
                Interface = "192.168.1.10",
                Metric = "25"
            }
        };

        TunRouteDiagnosticsBuilder.ApplyWindowsRouteTableDiagnostics(
            snapshot,
            "10.0.0.1",
            routes,
            tunDefault);

        Assert.True(snapshot.HasTunDefaultRoute);
        Assert.Equal("1", snapshot.TunDefaultMetric);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void Build_DeduplicatesProxyBypassRoutesAndCopiesDirectRoutes()
    {
        var snapshot = TunRouteDiagnosticsBuilder.Build(
            routeService: null,
            tunIpAddress: "10.0.0.1",
            proxyBypassRoutes: ["1.1.1.1", "1.1.1.1"],
            directBypassRoutes: ["8.8.8.8"],
            directBypassRouteCount: 3);

        Assert.Equal(["1.1.1.1"], snapshot.ProxyBypassRoutes);
        Assert.Equal(["8.8.8.8"], snapshot.DirectBypassRoutes);
        Assert.Equal(3, snapshot.DirectBypassRouteCount);
    }
}
