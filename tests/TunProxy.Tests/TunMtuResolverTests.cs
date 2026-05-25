using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunMtuResolverTests
{
    [Fact]
    public void ComputeAutoTunMtu_IncreasesWhenUpstreamIsLarge()
    {
        var mtu = TunMtuResolver.ComputeAutoTunMtu(2800);
        Assert.Equal(2600, mtu);
    }

    [Fact]
    public void ComputeAutoTunMtu_KeepsDefaultOnStandardUpstream()
    {
        var mtu = TunMtuResolver.ComputeAutoTunMtu(1500);
        Assert.Equal(1500, mtu);
    }

    [Fact]
    public void ComputeAutoTunMtu_DoesNotExceedUpstream()
    {
        var mtu = TunMtuResolver.ComputeAutoTunMtu(1400);
        Assert.Equal(1400, mtu);
    }

    [Fact]
    public void TryGetUpstreamMtu_SelectsMatchingNonTunInterface()
    {
        var bindAddress = IPAddress.Parse("10.0.0.10");
        var snapshots = new[]
        {
            new InterfaceSnapshot("TunProxy", 2600, true, false, true, [bindAddress]),
            new InterfaceSnapshot("ZeroTier One", 2800, true, false, false, [bindAddress])
        };

        var result = TunMtuResolver.TryGetUpstreamMtu(bindAddress, snapshots, out var mtu, out var interfaceName);

        Assert.True(result);
        Assert.Equal(2800, mtu);
        Assert.Equal("ZeroTier One", interfaceName);
    }

    [Fact]
    public void TryGetUpstreamMtu_ReturnsFalseWhenOnlyTunMatches()
    {
        var bindAddress = IPAddress.Parse("10.0.0.10");
        var snapshots = new[]
        {
            new InterfaceSnapshot("TunProxy", 2600, true, false, true, [bindAddress])
        };

        var result = TunMtuResolver.TryGetUpstreamMtu(bindAddress, snapshots, out var mtu, out var interfaceName);

        Assert.False(result);
        Assert.Equal(0, mtu);
        Assert.Equal(string.Empty, interfaceName);
    }
}
