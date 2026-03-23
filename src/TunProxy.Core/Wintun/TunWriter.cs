using System.Runtime.InteropServices;
using TunProxy.Core.Packets;

namespace TunProxy.Core.Wintun;

/// <summary>
/// TUN 设备数据包写入器
/// 消除 6 个 WriteXxxToTunAsync 方法中重复的 Allocate→Copy→Send 模式
/// </summary>
public static class TunWriter
{
    /// <summary>
    /// 将数据包写入 TUN 设备
    /// </summary>
    public static void WritePacket(WintunSession session, byte[] packetData)
    {
        if (packetData.Length == 0) return;

        var sendPacket = session.AllocateSendPacket((uint)packetData.Length);
        if (sendPacket == IntPtr.Zero) return;

        Marshal.Copy(packetData, 0, sendPacket, packetData.Length);
        session.SendPacket(sendPacket);
    }

    /// <summary>
    /// 写入 SYN-ACK 响应
    /// </summary>
    public static void WriteSynAck(WintunSession session, IPPacket requestPacket, out uint serverIsn)
    {
        WritePacket(session, PacketBuilder.BuildSynAck(requestPacket, out serverIsn));
    }

    /// <summary>
    /// 写入 RST 响应
    /// </summary>
    public static void WriteRst(WintunSession session, IPPacket requestPacket)
    {
        WritePacket(session, PacketBuilder.BuildRst(requestPacket));
    }

    /// <summary>
    /// 写入纯 ACK
    /// </summary>
    public static void WriteAck(WintunSession session, IPPacket requestPacket,
        uint seqNum, uint ackNum)
    {
        WritePacket(session, PacketBuilder.BuildAck(requestPacket, seqNum, ackNum));
    }

    /// <summary>
    /// 写入 FIN-ACK
    /// </summary>
    public static void WriteFinAck(WintunSession session, IPPacket requestPacket,
        uint seqNum, uint ackNum)
    {
        WritePacket(session, PacketBuilder.BuildFinAck(requestPacket, seqNum, ackNum));
    }

    /// <summary>
    /// 写入数据响应包（PSH+ACK，带明确序列号）
    /// </summary>
    public static void WriteDataResponse(WintunSession session, IPPacket synPacket,
        ReadOnlySpan<byte> data, uint serverSeq, uint clientAck)
    {
        WritePacket(session, PacketBuilder.BuildDataResponse(synPacket, data, serverSeq, clientAck));
    }

    /// <summary>
    /// 写入 UDP 响应包
    /// </summary>
    public static void WriteUdpResponse(WintunSession session, IPPacket requestPacket,
        ReadOnlySpan<byte> responseData)
    {
        WritePacket(session, PacketBuilder.BuildUdpResponse(requestPacket, responseData));
    }
}
