using System.Net;
using System.Text;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunConnectionDecisionsTests
{
    [Fact]
    public void SelectTarget_UsesHttpHostBeforeDnsCache()
    {
        var payload = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        var target = TunConnectionDecisions.SelectTarget(
            destPort: 80,
            destIp: "203.0.113.10",
            payload,
            cachedHostname: "cached.example");

        Assert.Equal("example.com", target.ConnectHost);
        Assert.Equal("Host", target.DomainSource);
        Assert.Equal("example.com", target.DomainHint);
        Assert.Equal("example.com", target.DnsCacheHost);
    }

    [Fact]
    public void SelectTarget_UsesDnsCacheWhenPayloadHasNoHost()
    {
        var target = TunConnectionDecisions.SelectTarget(
            destPort: 12345,
            destIp: "203.0.113.10",
            [],
            cachedHostname: "cached.example");

        Assert.Equal("cached.example", target.ConnectHost);
        Assert.Equal("DNS", target.DomainSource);
    }

    [Fact]
    public void SelectTarget_FallsBackToIpWithoutDomainHint()
    {
        var target = TunConnectionDecisions.SelectTarget(
            destPort: 12345,
            destIp: "203.0.113.10",
            [],
            cachedHostname: null);

        Assert.Equal("203.0.113.10", target.ConnectHost);
        Assert.Equal("IP", target.DomainSource);
        Assert.Null(target.DomainHint);
        Assert.Null(target.DnsCacheHost);
    }

    [Fact]
    public void SelectUpstreamHost_UsesEvaluatedIpForDirectRoute()
    {
        var target = new TunConnectionTarget("example.com", "DNS", "203.0.113.10");
        var decision = RouteDecision.Direct("ResolvedPrivateIP", "example.com", IPAddress.Parse("192.168.1.10"));

        Assert.Equal("192.168.1.10", TunConnectionDecisions.SelectUpstreamHost(false, decision, target));
        Assert.Equal("example.com", TunConnectionDecisions.SelectUpstreamHost(true, decision, target));
    }

    [Theory]
    [InlineData("Failed [PROXY_DENIED]", "PROXY", "proxy denied", true, false)]
    [InlineData("Failed [CONNECT_FAILED]", "PROXY", "connect failed", false, true)]
    [InlineData("Failed [CONNECT_FAILED]", "DIRECT", "connect failed", false, false)]
    [InlineData("boom", "PROXY", "error", false, false)]
    public void ClassifyFailure_MapsErrorReasonAndCacheSideEffects(
        string message,
        string routeLabel,
        string expectedReason,
        bool expectedBlocked,
        bool expectedConnectFailed)
    {
        var failure = TunConnectionDecisions.ClassifyFailure(new InvalidOperationException(message), routeLabel);

        Assert.Equal(expectedReason, failure.Reason);
        Assert.Equal(expectedBlocked, failure.ShouldRecordProxyBlocked);
        Assert.Equal(expectedConnectFailed, failure.ShouldRecordProxyConnectFailed);
    }
}
