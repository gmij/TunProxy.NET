using System.Net;
using System.Runtime.CompilerServices;

namespace TunProxy.Core.Packets;

/// <summary>
/// 网络协议辅助函数
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// 转换为网络字节序 (big-endian) 的 ushort
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort NetworkToHostOrder(ushort value)
    {
        return (ushort)IPAddress.NetworkToHostOrder((short)value);
    }

    /// <summary>
    /// 转换为主机字节序的 ushort
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort HostToNetworkOrder(ushort value)
    {
        return (ushort)IPAddress.HostToNetworkOrder((short)value);
    }

    /// <summary>
    /// 从网络字节序读取 ushort
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data)
    {
        return (ushort)((data[0] << 8) | data[1]);
    }

    /// <summary>
    /// 写入 ushort 到网络字节序
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt16BigEndian(Span<byte> data, ushort value)
    {
        data[0] = (byte)(value >> 8);
        data[1] = (byte)(value & 0xFF);
    }

    /// <summary>
    /// 读取 uint32 从网络字节序
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data)
    {
        return (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
    }

    /// <summary>
    /// 写入 uint32 到网络字节序
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt32BigEndian(Span<byte> data, uint value)
    {
        data[0] = (byte)(value >> 24);
        data[1] = (byte)(value >> 16);
        data[2] = (byte)(value >> 8);
        data[3] = (byte)(value & 0xFF);
    }

    /// <summary>
    /// 计算 IP 头部校验和
    /// </summary>
    public static ushort CalculateIPChecksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;

        // IP 头部长度必须是 4 的倍数
        for (int i = 0; i < header.Length; i += 2)
        {
            // 跳过校验和字段本身 (偏移 10-11)
            if (i == 10)
                continue;

            ushort word = ReadUInt16BigEndian(header.Slice(i, 2));
            sum += word;
        }

        // 将进位加回
        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>
    /// 计算 TCP/UDP 校验和
    /// </summary>
    public static ushort CalculateTcpUdpChecksum(
        ReadOnlySpan<byte> sourceIP,
        ReadOnlySpan<byte> destIP,
        byte protocol,
        ReadOnlySpan<byte> tcpUdpPacket)
    {
        uint sum = 0;

        // 伪头部：源 IP
        sum += ReadUInt16BigEndian(sourceIP.Slice(0, 2));
        sum += ReadUInt16BigEndian(sourceIP.Slice(2, 2));

        // 伪头部：目标 IP
        sum += ReadUInt16BigEndian(destIP.Slice(0, 2));
        sum += ReadUInt16BigEndian(destIP.Slice(2, 2));

        // 伪头部：协议
        sum += protocol;

        // 伪头部：TCP/UDP 长度
        sum += (ushort)tcpUdpPacket.Length;

        // TCP/UDP 数据
        for (int i = 0; i < tcpUdpPacket.Length; i += 2)
        {
            // 跳过校验和字段本身 (TCP 偏移 16-17, UDP 偏移 6-7)
            if ((protocol == 6 && i == 16) || (protocol == 17 && i == 6))
                continue;

            ushort word;
            if (i + 1 < tcpUdpPacket.Length)
            {
                word = ReadUInt16BigEndian(tcpUdpPacket.Slice(i, 2));
            }
            else
            {
                // 最后一个字节，需要补零
                word = (ushort)(tcpUdpPacket[i] << 8);
            }
            sum += word;
        }

        // 将进位加回
        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }
}
