using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunPacketDecisionsTests
{
    [Theory]
    [InlineData(new byte[] { 0x60 }, true)]
    [InlineData(new byte[] { 0x45 }, false)]
    [InlineData(new byte[] { }, false)]
    public void IsIpv6Packet_UsesPacketVersionNibble(byte[] data, bool expected)
    {
        Assert.Equal(expected, TunPacketDecisions.IsIpv6Packet(data));
    }

    [Fact]
    public void ShouldBufferInitialTlsPayload_WaitsForUncachedTlsClientHelloOnPort443()
    {
        var clientHelloPrefix = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x2f };

        Assert.True(TunPacketDecisions.ShouldBufferInitialTlsPayload(
            destPort: 443,
            payloadLength: clientHelloPrefix.Length,
            bufferedPayloadLength: 0,
            hasCachedHostname: false,
            clientHelloPrefix));
    }

    [Fact]
    public void ShouldBufferInitialTlsPayload_DoesNotWaitWhenHostnameIsAlreadyCached()
    {
        var clientHelloPrefix = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x2f };

        Assert.False(TunPacketDecisions.ShouldBufferInitialTlsPayload(
            destPort: 443,
            payloadLength: clientHelloPrefix.Length,
            bufferedPayloadLength: 0,
            hasCachedHostname: true,
            clientHelloPrefix));
    }

    [Theory]
    [InlineData("127.0.0.1", "10.0.0.1", "192.168.1.1", true)]
    [InlineData("10.0.0.1", "10.0.0.1", "192.168.1.1", true)]
    [InlineData("169.254.10.20", "10.0.0.1", "192.168.1.1", true)]
    [InlineData("192.168.1.1", "10.0.0.1", "192.168.1.1", true)]
    [InlineData("203.0.113.10", "10.0.0.1", "192.168.1.1", false)]
    public void ShouldSkipDirectBypassRoute_SkipsOnlyNonRoutableOrLocalAddresses(
        string destination,
        string tunIp,
        string gateway,
        bool expected)
    {
        Assert.Equal(
            expected,
            TunPacketDecisions.ShouldSkipDirectBypassRoute(
                IPAddress.Parse(destination),
                tunIp,
                gateway));
    }

    [Fact]
    public void ShouldSkipDirectBypassRoute_SkipsIpv6Addresses()
    {
        Assert.True(TunPacketDecisions.ShouldSkipDirectBypassRoute(
            IPAddress.Parse("2001:db8::1"),
            "10.0.0.1",
            "192.168.1.1"));
    }
}
