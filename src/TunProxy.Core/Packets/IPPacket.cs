using System.Net;
using System.Runtime.InteropServices;

namespace TunProxy.Core.Packets;

/// <summary>
/// IP 协议类型
/// </summary>
public enum IPProtocol : byte
{
    ICMP = 1,
    TCP = 6,
    UDP = 17
}

/// <summary>
/// IPv4 头部
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
public struct IPv4Header
{
    [FieldOffset(0)]
    public byte VersionAndIHL;

    [FieldOffset(1)]
    public byte TypeOfService;

    [FieldOffset(2)]
    public ushort TotalLength;

    [FieldOffset(4)]
    public ushort Identification;

    [FieldOffset(6)]
    public ushort FlagsAndFragmentOffset;

    [FieldOffset(8)]
    public byte TimeToLive;

    [FieldOffset(9)]
    public byte Protocol;

    [FieldOffset(10)]
    public ushort HeaderChecksum;

    [FieldOffset(12)]
    public uint SourceAddress;

    [FieldOffset(16)]
    public uint DestinationAddress;

    public byte Version => (byte)((VersionAndIHL >> 4) & 0x0F);
    public byte IHL => (byte)(VersionAndIHL & 0x0F);
    public byte HeaderLength => (byte)(IHL * 4);

    public IPAddress SourceIPAddress => new IPAddress(SourceAddress);
    public IPAddress DestinationIPAddress => new IPAddress(DestinationAddress);

    public IPProtocol ProtocolType => (IPProtocol)Protocol;
}

/// <summary>
/// TCP 头部
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
public struct TCPHeader
{
    [FieldOffset(0)]
    public ushort SourcePort;

    [FieldOffset(2)]
    public ushort DestinationPort;

    [FieldOffset(4)]
    public uint SequenceNumber;

    [FieldOffset(8)]
    public uint AcknowledgmentNumber;

    [FieldOffset(12)]
    public byte DataOffsetAndFlags;

    [FieldOffset(13)]
    public byte Flags;

    [FieldOffset(14)]
    public ushort Window;

    [FieldOffset(16)]
    public ushort Checksum;

    [FieldOffset(18)]
    public ushort UrgentPointer;

    public byte DataOffset => (byte)((DataOffsetAndFlags >> 4) & 0x0F);
    public ushort HeaderLength => (ushort)(DataOffset * 4);
}

/// <summary>
/// UDP 头部
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct UDPHeader
{
    [FieldOffset(0)]
    public ushort SourcePort;

    [FieldOffset(2)]
    public ushort DestinationPort;

    [FieldOffset(4)]
    public ushort Length;

    [FieldOffset(6)]
    public ushort Checksum;
}

/// <summary>
/// IP 数据包解析器
/// </summary>
public unsafe class IPPacket
{
    public IPv4Header Header { get; private set; }
    public byte[] Payload { get; private set; } = Array.Empty<byte>();
    public TCPHeader? TCPHeader { get; private set; }
    public UDPHeader? UDPHeader { get; private set; }

    public bool IsTCP => Header.ProtocolType == IPProtocol.TCP;
    public bool IsUDP => Header.ProtocolType == IPProtocol.UDP;
    public bool IsICMP => Header.ProtocolType == IPProtocol.ICMP;

    public ushort? SourcePort
    {
        get
        {
            if (TCPHeader.HasValue) return TCPHeader.Value.SourcePort;
            if (UDPHeader.HasValue) return UDPHeader.Value.SourcePort;
            return null;
        }
    }

    public ushort? DestinationPort
    {
        get
        {
            if (TCPHeader.HasValue) return TCPHeader.Value.DestinationPort;
            if (UDPHeader.HasValue) return UDPHeader.Value.DestinationPort;
            return null;
        }
    }

    public static IPPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
            return null;

        fixed (byte* ptr = data)
        {
            var header = Marshal.PtrToStructure<IPv4Header>((IntPtr)ptr);

            if (header.Version != 4)
                return null;

            var headerLength = header.HeaderLength;
            if (data.Length < headerLength)
                return null;

            var packet = new IPPacket
            {
                Header = header,
                Payload = data.Slice(headerLength).ToArray()
            };

            // 解析 TCP/UDP 头部
            if (header.ProtocolType == IPProtocol.TCP && data.Length >= headerLength + 20)
            {
                fixed (byte* tcpPtr = data.Slice(headerLength))
                {
                    packet.TCPHeader = Marshal.PtrToStructure<TCPHeader>((IntPtr)tcpPtr);
                }
            }
            else if (header.ProtocolType == IPProtocol.UDP && data.Length >= headerLength + 8)
            {
                fixed (byte* udpPtr = data.Slice(headerLength))
                {
                    packet.UDPHeader = Marshal.PtrToStructure<UDPHeader>((IntPtr)udpPtr);
                }
            }

            return packet;
        }
    }

    public byte[] BuildResponse(byte[] payload)
    {
        // 交换源和目标 IP
        var responseHeader = Header;
        var temp = responseHeader.SourceAddress;
        responseHeader.SourceAddress = responseHeader.DestinationAddress;
        responseHeader.DestinationAddress = temp;

        // 如果是 TCP/UDP，交换端口
        if (IsTCP && TCPHeader.HasValue)
        {
            var tcpHeader = TCPHeader.Value;
            var tempPort = tcpHeader.SourcePort;
            tcpHeader.SourcePort = tcpHeader.DestinationPort;
            tcpHeader.DestinationPort = tempPort;

            return BuildPacket(responseHeader, tcpHeader, payload);
        }
        else if (IsUDP && UDPHeader.HasValue)
        {
            var udpHeader = UDPHeader.Value;
            var tempPort = udpHeader.SourcePort;
            udpHeader.SourcePort = udpHeader.DestinationPort;
            udpHeader.DestinationPort = tempPort;

            return BuildPacket(responseHeader, udpHeader, payload);
        }

        return Array.Empty<byte>();
    }

    private byte[] BuildPacket(IPv4Header ipHeader, TCPHeader tcpHeader, byte[] payload)
    {
        var totalLength = 20 + 20 + payload.Length;
        var packet = new byte[totalLength];

        fixed (byte* ptr = packet)
        {
            // 写入 IP 头部
            var ipPtr = (IPv4Header*)ptr;
            *ipPtr = ipHeader;
            ipPtr->TotalLength = (ushort)totalLength;

            // 写入 TCP 头部
            var tcpPtr = (TCPHeader*)(ptr + 20);
            *tcpPtr = tcpHeader;

            // 写入 payload
            Marshal.Copy(payload, 0, (IntPtr)(ptr + 40), payload.Length);
        }

        return packet;
    }

    private byte[] BuildPacket(IPv4Header ipHeader, UDPHeader udpHeader, byte[] payload)
    {
        var totalLength = 20 + 8 + payload.Length;
        var packet = new byte[totalLength];

        fixed (byte* ptr = packet)
        {
            // 写入 IP 头部
            var ipPtr = (IPv4Header*)ptr;
            *ipPtr = ipHeader;
            ipPtr->TotalLength = (ushort)totalLength;

            // 写入 UDP 头部
            var udpPtr = (UDPHeader*)(ptr + 20);
            *udpPtr = udpHeader;
            udpPtr->Length = (ushort)(8 + payload.Length);

            // 写入 payload
            Marshal.Copy(payload, 0, (IntPtr)(ptr + 28), payload.Length);
        }

        return packet;
    }
}
