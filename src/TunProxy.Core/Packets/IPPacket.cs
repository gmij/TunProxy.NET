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
/// IP 数据包解析器
/// </summary>
public class IPPacket
{
    public IPv4HeaderInfo Header { get; private set; }
    public byte[] Payload { get; private set; } = Array.Empty<byte>();
    public TCPHeaderInfo? TCPHeader { get; private set; }
    public UDPHeaderInfo? UDPHeader { get; private set; }

    private IPPacket()
    {
        Header = new IPv4HeaderInfo();
    }

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

        // 解析 IPv4 头部
        byte versionAndIhl = data[0];
        byte version = (byte)((versionAndIhl >> 4) & 0x0F);
        
        if (version != 4)
            return null;

        byte ihl = (byte)(versionAndIhl & 0x0F);
        byte headerLength = (byte)(ihl * 4);

        if (data.Length < headerLength)
            return null;

        var header = new IPv4HeaderInfo
        {
            Version = version,
            IHL = ihl,
            TotalLength = BitConverter.ToUInt16(data.Slice(2, 2)),
            Protocol = data[9],
            SourceAddress = new IPAddress(data.Slice(12, 4)),
            DestinationAddress = new IPAddress(data.Slice(16, 4))
        };

        var packet = new IPPacket
        {
            Header = header,
            Payload = data.Slice(headerLength).ToArray()
        };

        // 解析 TCP/UDP 头部
        if (header.ProtocolType == IPProtocol.TCP && data.Length >= headerLength + 20)
        {
            packet.TCPHeader = new TCPHeaderInfo
            {
                SourcePort = BitConverter.ToUInt16(data.Slice(headerLength, 2)),
                DestinationPort = BitConverter.ToUInt16(data.Slice(headerLength + 2, 2))
            };
        }
        else if (header.ProtocolType == IPProtocol.UDP && data.Length >= headerLength + 8)
        {
            packet.UDPHeader = new UDPHeaderInfo
            {
                SourcePort = BitConverter.ToUInt16(data.Slice(headerLength, 2)),
                DestinationPort = BitConverter.ToUInt16(data.Slice(headerLength + 2, 2)),
                Length = BitConverter.ToUInt16(data.Slice(headerLength + 4, 2))
            };
        }

        return packet;
    }
}

/// <summary>
/// IPv4 头部信息
/// </summary>
public struct IPv4HeaderInfo
{
    public byte Version { get; set; }
    public byte IHL { get; set; }
    public ushort TotalLength { get; set; }
    public byte Protocol { get; set; }
    public IPAddress SourceAddress { get; set; }
    public IPAddress DestinationAddress { get; set; }

    public IPProtocol ProtocolType => (IPProtocol)Protocol;
}

/// <summary>
/// TCP 头部信息
/// </summary>
public struct TCPHeaderInfo
{
    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
}

/// <summary>
/// UDP 头部信息
/// </summary>
public struct UDPHeaderInfo
{
    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
    public ushort Length { get; set; }
}
