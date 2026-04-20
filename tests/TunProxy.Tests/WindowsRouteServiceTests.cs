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
}
