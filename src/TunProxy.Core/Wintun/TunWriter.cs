using TunProxy.Core.Packets;
using TunProxy.Core.Tun;

namespace TunProxy.Core.Wintun;

/// <summary>
/// TUN 设备数据包写入器
/// </summary>
public static class TunWriter
{
    /// <summary>
    /// 将数据包写入 TUN 设备
    /// </summary>
    public static void WritePacket(ITunDevice device, byte[] packetData)
    {
        if (packetData.Length == 0) return;
        device.WritePacket(packetData);
    }

    /// <summary>
    /// 写入 SYN-ACK 响应
    /// </summary>
    public static void WriteSynAck(ITunDevice device, IPPacket requestPacket, out uint serverIsn)
    {
        WritePacket(device, PacketBuilder.BuildSynAck(requestPacket, out serverIsn));
    }

    /// <summary>
    /// 写入 RST 响应
    /// </summary>
    public static void WriteRst(ITunDevice device, IPPacket requestPacket)
    {
        WritePacket(device, PacketBuilder.BuildRst(requestPacket));
    }

    /// <summary>
    /// 写入纯 ACK
    /// </summary>
    public static void WriteAck(ITunDevice device, IPPacket requestPacket,
        uint seqNum, uint ackNum)
    {
        WritePacket(device, PacketBuilder.BuildAck(requestPacket, seqNum, ackNum));
    }

    /// <summary>
    /// 写入 FIN-ACK
    /// </summary>
    public static void WriteFinAck(ITunDevice device, IPPacket requestPacket,
        uint seqNum, uint ackNum)
    {
        WritePacket(device, PacketBuilder.BuildFinAck(requestPacket, seqNum, ackNum));
    }

    /// <summary>
    /// 写入数据响应包（PSH+ACK，带明确序列号）
    /// </summary>
    public static void WriteDataResponse(ITunDevice device, IPPacket synPacket,
        ReadOnlySpan<byte> data, uint serverSeq, uint clientAck)
    {
        WritePacket(device, PacketBuilder.BuildDataResponse(synPacket, data, serverSeq, clientAck));
    }

    /// <summary>
    /// 写入 UDP 响应包
    /// </summary>
    public static void WriteUdpResponse(ITunDevice device, IPPacket requestPacket,
        ReadOnlySpan<byte> responseData)
    {
        WritePacket(device, PacketBuilder.BuildUdpResponse(requestPacket, responseData));
    }

    /// <summary>
    /// 写入 ICMP Port Unreachable（用于拒绝 QUIC/UDP，迫使浏览器快速回退到 TCP）
    /// </summary>
    public static void WriteIcmpPortUnreachable(ITunDevice device, IPPacket udpPacket)
    {
        WritePacket(device, PacketBuilder.BuildIcmpPortUnreachable(udpPacket));
    }
}
