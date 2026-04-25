using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class DirectBypassRouteSchedulerTests
{
    [Fact]
    public void ShouldEnsureDirectBypassRoute_ReturnsFalseWithoutRouteService()
    {
        Assert.False(DirectBypassRouteScheduler.ShouldEnsureDirectBypassRoute(
            IPAddress.Parse("203.0.113.10"),
            hasRouteService: false,
            isProxyBypassRoute: false,
            tunIpAddress: "10.0.0.1",
            originalDefaultGateway: "192.168.1.1"));
    }

    [Fact]
    public void ShouldEnsureDirectBypassRoute_ReturnsFalseForProxyBypassRoute()
    {
        Assert.False(DirectBypassRouteScheduler.ShouldEnsureDirectBypassRoute(
            IPAddress.Parse("203.0.113.10"),
            hasRouteService: true,
            isProxyBypassRoute: true,
            tunIpAddress: "10.0.0.1",
            originalDefaultGateway: "192.168.1.1"));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.10.20", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("203.0.113.10", true)]
    public void ShouldEnsureDirectBypassRoute_DelegatesDestinationSafetyRules(
        string destination,
        bool expected)
    {
        Assert.Equal(
            expected,
            DirectBypassRouteScheduler.ShouldEnsureDirectBypassRoute(
                IPAddress.Parse(destination),
                hasRouteService: true,
                isProxyBypassRoute: false,
                tunIpAddress: "10.0.0.1",
                originalDefaultGateway: "192.168.1.1"));
    }
}
