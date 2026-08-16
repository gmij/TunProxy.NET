using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunProxyServiceTests
{
    [Fact]
    public void BuildStartupDirectDnsServerCandidates_IncludesSystemDnsAndConfiguredTunDns()
    {
        var servers = TunProxyService.BuildStartupDirectDnsServerCandidates(
            ["10.255.0.2", "192.168.1.1", "not-an-ip"],
            "10.255.0.3");

        Assert.Equal(4, servers.Count);
        Assert.Contains(DnsProxyService.DefaultDomesticDns, servers);
        Assert.Contains("10.255.0.3", servers);
        Assert.Contains("10.255.0.2", servers);
        Assert.Contains("192.168.1.1", servers);
        Assert.DoesNotContain("not-an-ip", servers);
    }
}
