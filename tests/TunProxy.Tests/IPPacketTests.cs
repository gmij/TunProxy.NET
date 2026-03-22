using TunProxy.Core.Packets;

namespace TunProxy.Tests;

public class IPPacketTests
{
    [Fact]
    public void Parse_ValidIPv4Packet_ReturnsPacket()
    {
        // 构造一个简单的 IPv4 数据包（TCP，源端口 12345，目标端口 80）
        // 注意：网络字节序是大端（big-endian）
        var packetData = new byte[]
        {
            // IPv4 头部（20 字节）- 网络字节序（大端）
            0x45, 0x00, 0x00, 0x2D, // 版本 4, IHL 5, TOS 0, 总长度 45 (大端：0x002D = 20+20+5)
            0x00, 0x01, 0x00, 0x00, // 标识
            0x40, 0x06, 0x00, 0x00, // TTL 64, 协议 6 (TCP), 校验和
            0x0A, 0x00, 0x00, 0x01, // 源 IP: 10.0.0.1
            0x0A, 0x00, 0x00, 0x02, // 目标 IP: 10.0.0.2

            // TCP 头部（20 字节）- 网络字节序（大端）
            0x30, 0x39, // 源端口：12345 (0x3039 大端)
            0x00, 0x50, // 目标端口：80 (0x0050 大端)
            0x00, 0x00, 0x00, 0x01, // 序列号
            0x00, 0x00, 0x00, 0x00, // 确认号
            0x50, 0x02, 0xFF, 0xFF, // 数据偏移 5, 标志 SYN, 窗口
            0x00, 0x00, 0x00, 0x00, // 校验和，紧急指针

            // Payload
            0x48, 0x65, 0x6C, 0x6C, 0x6F // "Hello"
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
            0x60, 0x00, 0x00, 0x00, // IPv6 版本 6
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        var packet = IPPacket.Parse(packetData);

        Assert.Null(packet);
    }

    [Fact]
    public void Parse_TooShort_ReturnsNull()
    {
        var packetData = new byte[] { 0x45, 0x00, 0x00 };

        var packet = IPPacket.Parse(packetData);

        Assert.Null(packet);
    }

    [Fact]
    public void Parse_UDPPacket_ReturnsPacketWithUDPHeader()
    {
        var packetData = new byte[]
        {
            // IPv4 头部 - 网络字节序（大端）
            0x45, 0x00, 0x00, 0x1C,
            0x00, 0x01, 0x00, 0x00,
            0x40, 0x11, 0x00, 0x00, // 协议 17 (UDP)
            0x0A, 0x00, 0x00, 0x01, // 源 IP
            0x0A, 0x00, 0x00, 0x02, // 目标 IP

            // UDP 头部（8 字节）- 网络字节序（大端）
            0x00, 0x35, // 源端口：53 (DNS) 大端
            0x00, 0x35, // 目标端口：53 大端
            0x00, 0x0C, // 长度大端 (8 + 4 = 12)
            0x00, 0x00, // 校验和

            // Payload
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
    public void Parse_ICMPPacket_ReturnsPacketWithoutPort()
    {
        var packetData = new byte[]
        {
            // IPv4 头部
            0x45, 0x00, 0x00, 0x1C,
            0x00, 0x01, 0x00, 0x00,
            0x40, 0x01, 0x00, 0x00, // 协议 1 (ICMP)
            0x0A, 0x00, 0x00, 0x01,
            0x0A, 0x00, 0x00, 0x02,
            
            // ICMP 头部
            0x08, 0x00, 0x00, 0x00
        };

        var packet = IPPacket.Parse(packetData);

        Assert.NotNull(packet);
        Assert.True(packet.IsICMP);
        Assert.Null(packet.SourcePort);
        Assert.Null(packet.DestinationPort);
    }
}
