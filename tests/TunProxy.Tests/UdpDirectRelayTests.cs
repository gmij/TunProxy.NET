using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Packets;

namespace TunProxy.Tests;

public class UdpDirectRelayTests
{
    [Fact]
    public void CleanupExpired_RemovesNoSessionsWhenNoneExist()
    {
        using var relay = new UdpDirectRelay();

        // Should not throw.
        relay.CleanupExpired(TimeSpan.FromSeconds(60));

        Assert.Equal(0, relay.Count);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var relay = new UdpDirectRelay();
        relay.Dispose();
        relay.Dispose(); // Must not throw.
    }

    [Fact]
    public async Task ForwardAsync_ReturnsImmediatelyAfterDispose()
    {
        var relay = new UdpDirectRelay();
        relay.Dispose();

        // ForwardAsync after disposal must complete without throwing.
        var packet = MakeUdpPacket("10.0.0.2", 12345, "8.8.8.8", 53, [0xAB]);
        await relay.ForwardAsync(null!, packet, null, CancellationToken.None);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal UDP <see cref="IPPacket"/> for use in tests.
    /// </summary>
    private static IPPacket MakeUdpPacket(
        string srcIp, ushort srcPort,
        string dstIp, ushort dstPort,
        byte[] payload)
    {
        var src = IPAddress.Parse(srcIp);
        var dst = IPAddress.Parse(dstIp);
        var raw = PacketBuilder.BuildUdpPacket(
            src.GetAddressBytes(), dst.GetAddressBytes(),
            srcPort, dstPort, payload);

        // Re-parse so we get the same IPPacket fields that production code uses.
        var parsed = IPPacket.Parse(raw);
        Assert.NotNull(parsed);
        return parsed!;
    }
}
