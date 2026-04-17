using TunProxy.Core.Packets;

namespace TunProxy.Tests;

public class IPPacketTests
{
    [Fact]
    public void Parse_ValidTcpPacket_ReturnsPacket()
    {
        var packetData = new byte[]
        {
            0x45, 0x00, 0x00, 0x2D,
            0x00, 0x01, 0x00, 0x00,
            0x40, 0x06, 0x00, 0x00,
            0x0A, 0x00, 0x00, 0x01,
            0x0A, 0x00, 0x00, 0x02,
            0x30, 0x39,
            0x00, 0x50,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x50, 0x02, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x00,
            0x48, 0x65, 0x6C, 0x6C, 0x6F
        };

        var packet = IPPacket.Parse(packetData);

        Assert.NotNull(packet);
        Assert.Equal(4, packet.Header.Version);
        Assert.Equal(IPProtocol.TCP, packet.Header.ProtocolType);
        Assert.True(packet.IsTCP);
        Assert.Equal((ushort)12345, packet.SourcePort);
        Assert.Equal((ushort)80, packet.DestinationPort);
        Assert.Equal("10.0.0.1", packet.Header.SourceAddress.ToString());
        Assert.Equal("10.0.0.2", packet.Header.DestinationAddress.ToString());
        Assert.Equal(5, packet.Payload.Length);
    }

    [Fact]
    public void Parse_InvalidVersion_ReturnsNull()
    {
        var packetData = new byte[]
        {
            0x60, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        Assert.Null(IPPacket.Parse(packetData));
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        Assert.Null(IPPacket.Parse(new byte[] { 0x45, 0x00, 0x00 }));
    }

    [Fact]
    public void Parse_ValidUdpPacket_ReturnsPacketWithUdpHeader()
    {
        var packetData = new byte[]
        {
            0x45, 0x00, 0x00, 0x20,
            0x00, 0x01, 0x00, 0x00,
            0x40, 0x11, 0x00, 0x00,
            0x0A, 0x00, 0x00, 0x01,
            0x0A, 0x00, 0x00, 0x02,
            0x00, 0x35,
            0x00, 0x35,
            0x00, 0x0C,
            0x00, 0x00,
            0x00, 0x01, 0x02, 0x03
        };

        var packet = IPPacket.Parse(packetData);

        Assert.NotNull(packet);
        Assert.True(packet.IsUDP);
        Assert.Equal((ushort)53, packet.SourcePort);
        Assert.Equal((ushort)53, packet.DestinationPort);
        Assert.NotNull(packet.UDPHeader);
    }

    [Fact]
    public void Parse_IcmpPacket_ReturnsPacketWithoutPorts()
    {
        var packetData = new byte[]
        {
            0x45, 0x00, 0x00, 0x18,
            0x00, 0x01, 0x00, 0x00,
            0x40, 0x01, 0x00, 0x00,
            0x0A, 0x00, 0x00, 0x01,
            0x0A, 0x00, 0x00, 0x02,
            0x08, 0x00, 0x00, 0x00
        };

        var packet = IPPacket.Parse(packetData);

        Assert.NotNull(packet);
        Assert.True(packet.IsICMP);
        Assert.Null(packet.SourcePort);
        Assert.Null(packet.DestinationPort);
    }

    [Fact]
    public void Parse_TotalLengthExceedsBuffer_ReturnsNull()
    {
        var packetData = CreateTcpPacket(totalLength: 60, tcpDataOffset: 0x50, payload: [0x41, 0x42])[..40];

        Assert.Null(IPPacket.Parse(packetData));
    }

    [Fact]
    public void Parse_InvalidTcpHeaderLength_ReturnsNull()
    {
        var packetData = CreateTcpPacket(totalLength: 40, tcpDataOffset: 0x40, payload: []);

        Assert.Null(IPPacket.Parse(packetData));
    }

    [Fact]
    public void Parse_IgnoresTrailingBytesBeyondIpTotalLength()
    {
        var packetData = CreateTcpPacket(totalLength: 40, tcpDataOffset: 0x50, payload: []);
        var withTrailingBytes = packetData.Concat(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }).ToArray();

        var packet = IPPacket.Parse(withTrailingBytes);

        Assert.NotNull(packet);
        Assert.Empty(packet.Payload);
    }

    private static byte[] CreateTcpPacket(ushort totalLength, byte tcpDataOffset, byte[] payload)
    {
        var packet = new byte[Math.Max(totalLength, (ushort)40)];
        packet[0] = 0x45;
        packet[2] = (byte)(totalLength >> 8);
        packet[3] = (byte)(totalLength & 0xFF);
        packet[8] = 0x40;
        packet[9] = 0x06;
        packet[12] = 0x0A;
        packet[15] = 0x01;
        packet[16] = 0x0A;
        packet[19] = 0x02;
        packet[20] = 0x00;
        packet[21] = 0x50;
        packet[22] = 0x01;
        packet[23] = 0xBB;
        packet[32] = tcpDataOffset;
        packet[33] = 0x18;

        if (payload.Length > 0)
            Array.Copy(payload, 0, packet, 40, Math.Min(payload.Length, packet.Length - 40));

        return packet;
    }
}
