using System.Net;

namespace TunProxy.Core.Packets;

public enum IPProtocol : byte
{
    ICMP = 1,
    TCP = 6,
    UDP = 17
}

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
            if (TCPHeader.HasValue)
                return TCPHeader.Value.SourcePort;
            if (UDPHeader.HasValue)
                return UDPHeader.Value.SourcePort;
            return null;
        }
    }

    public ushort? DestinationPort
    {
        get
        {
            if (TCPHeader.HasValue)
                return TCPHeader.Value.DestinationPort;
            if (UDPHeader.HasValue)
                return UDPHeader.Value.DestinationPort;
            return null;
        }
    }

    public static IPPacket? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
            return null;

        byte versionAndIhl = data[0];
        byte version = (byte)((versionAndIhl >> 4) & 0x0F);
        if (version != 4)
            return null;

        byte ihl = (byte)(versionAndIhl & 0x0F);
        if (ihl < 5)
            return null;

        byte headerLength = (byte)(ihl * 4);
        if (data.Length < headerLength)
            return null;

        ushort totalLength = NetworkHelper.ReadUInt16BigEndian(data.Slice(2, 2));
        if (totalLength < headerLength || totalLength > data.Length)
            return null;

        var packetData = data[..totalLength];
        var header = new IPv4HeaderInfo
        {
            Version = version,
            IHL = ihl,
            TotalLength = totalLength,
            Protocol = packetData[9],
            SourceAddress = new IPAddress(packetData.Slice(12, 4)),
            DestinationAddress = new IPAddress(packetData.Slice(16, 4))
        };

        int transportHeaderLength = 0;
        TCPHeaderInfo? tcpHeader = null;
        UDPHeaderInfo? udpHeader = null;

        if (header.ProtocolType == IPProtocol.TCP)
        {
            if (totalLength < headerLength + 20)
                return null;

            tcpHeader = new TCPHeaderInfo
            {
                SourcePort = NetworkHelper.ReadUInt16BigEndian(packetData.Slice(headerLength, 2)),
                DestinationPort = NetworkHelper.ReadUInt16BigEndian(packetData.Slice(headerLength + 2, 2)),
                SequenceNumber = NetworkHelper.ReadUInt32BigEndian(packetData.Slice(headerLength + 4, 4)),
                AckNumber = NetworkHelper.ReadUInt32BigEndian(packetData.Slice(headerLength + 8, 4)),
                Flags = packetData[headerLength + 13]
            };

            byte dataOffset = packetData[headerLength + 12];
            byte tcpHeaderLen = (byte)((dataOffset >> 4) * 4);
            if (tcpHeaderLen < 20 || headerLength + tcpHeaderLen > totalLength)
                return null;

            transportHeaderLength = tcpHeaderLen;
        }
        else if (header.ProtocolType == IPProtocol.UDP)
        {
            if (totalLength < headerLength + 8)
                return null;

            udpHeader = new UDPHeaderInfo
            {
                SourcePort = NetworkHelper.ReadUInt16BigEndian(packetData.Slice(headerLength, 2)),
                DestinationPort = NetworkHelper.ReadUInt16BigEndian(packetData.Slice(headerLength + 2, 2)),
                Length = NetworkHelper.ReadUInt16BigEndian(packetData.Slice(headerLength + 4, 2))
            };
            if (udpHeader.Value.Length < 8 || headerLength + udpHeader.Value.Length > totalLength)
                return null;

            transportHeaderLength = 8;
        }

        int payloadStart = headerLength + transportHeaderLength;
        byte[] payload = totalLength > payloadStart
            ? packetData.Slice(payloadStart, totalLength - payloadStart).ToArray()
            : Array.Empty<byte>();

        return new IPPacket
        {
            Header = header,
            Payload = payload,
            TCPHeader = tcpHeader,
            UDPHeader = udpHeader
        };
    }
}

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

public struct TCPHeaderInfo
{
    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
    public uint SequenceNumber { get; set; }
    public uint AckNumber { get; set; }
    public byte Flags { get; set; }

    public bool FIN => (Flags & 0x01) != 0;
    public bool SYN => (Flags & 0x02) != 0;
    public bool RST => (Flags & 0x04) != 0;
    public bool PSH => (Flags & 0x08) != 0;
    public bool ACK => (Flags & 0x10) != 0;
    public bool URG => (Flags & 0x20) != 0;
}

public struct UDPHeaderInfo
{
    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
    public ushort Length { get; set; }
}
