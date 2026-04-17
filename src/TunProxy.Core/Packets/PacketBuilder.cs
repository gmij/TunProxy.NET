namespace TunProxy.Core.Packets;

/// <summary>
/// 统一的 TCP/UDP/IP 数据包构建器
/// 消除 TunProxyService 中 6 个 Build*Packet 方法的重复代码
/// </summary>
public static class PacketBuilder
{
    /// <summary>
    /// TCP 标志位常量
    /// </summary>
    [Flags]
    public enum TcpFlags : byte
    {
        FIN = 0x01,
        SYN = 0x02,
        RST = 0x04,
        PSH = 0x08,
        ACK = 0x10,
        URG = 0x20,
    }

    /// <summary>
    /// 构建 TCP 数据包（统一入口，消除所有 Build*Packet 重复）
    /// </summary>
    /// <param name="requestPacket">原始请求包（用于提取 src/dst IP/Port）</param>
    /// <param name="flags">TCP 标志位</param>
    /// <param name="seqNum">序列号</param>
    /// <param name="ackNum">确认号</param>
    /// <param name="payload">负载数据（可选）</param>
    /// <param name="windowSize">窗口大小</param>
    /// <returns>完整的 IP+TCP 数据包</returns>
    public static byte[] BuildTcpPacket(
        IPPacket requestPacket,
        TcpFlags flags,
        uint seqNum,
        uint ackNum,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535)
    {
        // 注意：响应包的 src/dst 与请求包相反
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();
        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        return BuildTcpPacketRaw(sourceIP, destIP, sourcePort, destPort,
            flags, seqNum, ackNum, payload, windowSize);
    }

    /// <summary>
    /// 底层构建方法（直接使用 IP/Port 字节）
    /// </summary>
    public static byte[] BuildTcpPacketRaw(
        byte[] sourceIP,
        byte[] destIP,
        ushort sourcePort,
        ushort destPort,
        TcpFlags flags,
        uint seqNum,
        uint ackNum,
        ReadOnlySpan<byte> payload = default,
        ushort windowSize = 65535)
    {
        // --- TCP Header (20 bytes) ---
        var tcpHeader = new byte[20];
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(2, 2), destPort);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), seqNum);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);
        tcpHeader[12] = 0x50;           // Data Offset = 5 (20 bytes)
        tcpHeader[13] = (byte)flags;
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), windowSize);
        // Checksum (offset 16-17) and Urgent Pointer (18-19) remain 0

        // --- IP Header (20 bytes) ---
        var totalLength = (ushort)(20 + 20 + payload.Length);
        var ipHeader = BuildIPv4Header(sourceIP, destIP, 0x06 /* TCP */, totalLength);

        // --- TCP Checksum (needs pseudo-header + full TCP segment) ---
        var tcpSegment = new byte[20 + payload.Length];
        Array.Copy(tcpHeader, 0, tcpSegment, 0, 20);
        if (payload.Length > 0)
            payload.CopyTo(tcpSegment.AsSpan(20));

        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 6, tcpSegment);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);
        // Update checksum in tcpSegment too
        Array.Copy(tcpHeader, 16, tcpSegment, 16, 2);

        // --- Assemble final packet ---
        var packet = new byte[totalLength];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpSegment, 0, packet, 20, tcpSegment.Length);
        return packet;
    }

    /// <summary>
    /// 构建 SYN-ACK 响应包（含 MSS TCP Option，通告 MSS=1460）
    /// </summary>
    public static byte[] BuildSynAck(IPPacket requestPacket, out uint serverIsn)
    {
        serverIsn = (uint)Random.Shared.Next();
        uint clientSeq = requestPacket.TCPHeader?.SequenceNumber ?? 0;

        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP   = requestPacket.Header.SourceAddress.GetAddressBytes();
        var srcPort  = requestPacket.DestinationPort!.Value;
        var dstPort  = requestPacket.SourcePort!.Value;

        // TCP header = 24 bytes (20 fixed + 4 MSS option), DataOffset = 6
        var tcpHeader = new byte[24];
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(0, 2), srcPort);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(2, 2), dstPort);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), serverIsn);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), clientSeq + 1);
        tcpHeader[12] = 0x60;                              // DataOffset = 6 (24 bytes)
        tcpHeader[13] = (byte)(TcpFlags.SYN | TcpFlags.ACK);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), 65535); // Window
        // [16-17] Checksum filled below; [18-19] Urgent = 0
        // MSS option at byte 20: Kind=2, Len=4, MSS=1460 (0x05B4)
        tcpHeader[20] = 0x02;
        tcpHeader[21] = 0x04;
        tcpHeader[22] = 0x05;
        tcpHeader[23] = 0xB4;

        var totalLength = (ushort)(20 + 24);
        var ipHeader    = BuildIPv4Header(sourceIP, destIP, 0x06, totalLength);

        var checksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 6, tcpHeader);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), checksum);

        var packet = new byte[totalLength];
        Array.Copy(ipHeader,   0, packet,  0, 20);
        Array.Copy(tcpHeader,  0, packet, 20, 24);
        return packet;
    }

    /// <summary>
    /// 构建 ICMP Destination Unreachable / Port Unreachable 包
    /// 用于拒绝非 DNS 的 UDP 流量（强制浏览器 QUIC → TCP 快速回退）
    /// </summary>
    public static byte[] BuildIcmpPortUnreachable(IPPacket originalUdpPacket)
    {
        var sourceIP = originalUdpPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP   = originalUdpPacket.Header.SourceAddress.GetAddressBytes();

        // 重建原始 IP 头（20 bytes），用于 ICMP payload
        var origIpHdr = new byte[20];
        origIpHdr[0] = 0x45;
        var origLen = (ushort)(20 + 8 + originalUdpPacket.Payload.Length);
        NetworkHelper.WriteUInt16BigEndian(origIpHdr.AsSpan(2, 2), origLen);
        origIpHdr[8] = 0x40; // TTL
        origIpHdr[9] = 17;   // UDP
        Array.Copy(originalUdpPacket.Header.SourceAddress.GetAddressBytes(),      0, origIpHdr, 12, 4);
        Array.Copy(originalUdpPacket.Header.DestinationAddress.GetAddressBytes(), 0, origIpHdr, 16, 4);
        var origIpChecksum = NetworkHelper.CalculateIPChecksum(origIpHdr);
        NetworkHelper.WriteUInt16BigEndian(origIpHdr.AsSpan(10, 2), origIpChecksum);

        // 原始 UDP 头（8 bytes）
        var origUdpHdr = new byte[8];
        NetworkHelper.WriteUInt16BigEndian(origUdpHdr.AsSpan(0, 2), originalUdpPacket.SourcePort!.Value);
        NetworkHelper.WriteUInt16BigEndian(origUdpHdr.AsSpan(2, 2), originalUdpPacket.DestinationPort!.Value);
        NetworkHelper.WriteUInt16BigEndian(origUdpHdr.AsSpan(4, 2), (ushort)(8 + originalUdpPacket.Payload.Length));

        // ICMP 消息 = 8 字节头 + 20 字节原始 IP + 8 字节原始 UDP = 36 bytes
        var icmpData = new byte[36];
        icmpData[0] = 3; // Type: Destination Unreachable
        icmpData[1] = 3; // Code: Port Unreachable
        // [2-3] Checksum (填充在后), [4-7] Unused = 0
        Array.Copy(origIpHdr,  0, icmpData,  8, 20);
        Array.Copy(origUdpHdr, 0, icmpData, 28,  8);

        // 计算 ICMP 校验和（普通 internet checksum，无伪头部）
        uint sum = 0;
        for (int i = 0; i < icmpData.Length; i += 2)
        {
            ushort word = (i + 1 < icmpData.Length)
                ? NetworkHelper.ReadUInt16BigEndian(icmpData.AsSpan(i, 2))
                : (ushort)(icmpData[i] << 8);
            sum += word;
        }
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        NetworkHelper.WriteUInt16BigEndian(icmpData.AsSpan(2, 2), (ushort)~sum);

        var totalLength = (ushort)(20 + icmpData.Length);
        var ipHeader    = BuildIPv4Header(sourceIP, destIP, 0x01 /* ICMP */, totalLength);

        var packet = new byte[totalLength];
        Array.Copy(ipHeader,  0, packet,  0, 20);
        Array.Copy(icmpData,  0, packet, 20, icmpData.Length);
        return packet;
    }

    /// <summary>
    /// 构建 RST+ACK 响应包
    /// </summary>
    public static byte[] BuildRst(IPPacket requestPacket)
    {
        uint clientSeq = requestPacket.TCPHeader?.SequenceNumber ?? 0;

        return BuildTcpPacket(requestPacket,
            TcpFlags.RST | TcpFlags.ACK,
            seqNum: 0,
            ackNum: clientSeq + 1,
            windowSize: 0);
    }

    /// <summary>
    /// 构建纯 ACK 包（无载荷）
    /// </summary>
    public static byte[] BuildAck(IPPacket requestPacket, uint seqNum, uint ackNum)
    {
        return BuildTcpPacket(requestPacket, TcpFlags.ACK, seqNum, ackNum);
    }

    /// <summary>
    /// 构建 FIN+ACK 包
    /// </summary>
    public static byte[] BuildFinAck(IPPacket requestPacket, uint seqNum, uint ackNum)
    {
        return BuildTcpPacket(requestPacket,
            TcpFlags.FIN | TcpFlags.ACK,
            seqNum, ackNum,
            windowSize: 0);
    }

    /// <summary>
    /// 构建 PSH+ACK 数据包
    /// </summary>
    public static byte[] BuildDataResponse(IPPacket requestPacket, ReadOnlySpan<byte> data,
        uint seqNum, uint ackNum)
    {
        return BuildTcpPacket(requestPacket,
            TcpFlags.PSH | TcpFlags.ACK,
            seqNum, ackNum, data);
    }

    /// <summary>
    /// 构建基于请求包自动计算 seq/ack 的数据响应包
    /// </summary>
    public static byte[] BuildAutoResponse(IPPacket requestPacket, ReadOnlySpan<byte> responseData)
    {
        uint seqNum = requestPacket.TCPHeader?.AckNumber ?? 0;
        uint ackNum = requestPacket.TCPHeader.HasValue
            ? requestPacket.TCPHeader.Value.SequenceNumber + (uint)Math.Max(1, requestPacket.Payload.Length)
            : 0;

        return BuildDataResponse(requestPacket, responseData, seqNum, ackNum);
    }

    /// <summary>
    /// 构建 UDP 响应包
    /// </summary>
    public static byte[] BuildUdpResponse(IPPacket requestPacket, ReadOnlySpan<byte> responseData)
    {
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();
        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        // --- UDP Header (8 bytes) ---
        var udpHeader = new byte[8];
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(2, 2), destPort);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(4, 2), (ushort)(8 + responseData.Length));
        // Checksum (offset 6-7) starts at 0

        // --- IP Header ---
        var totalLength = (ushort)(20 + 8 + responseData.Length);
        var ipHeader = BuildIPv4Header(sourceIP, destIP, 0x11 /* UDP */, totalLength);

        // --- UDP Checksum ---
        var udpSegment = new byte[8 + responseData.Length];
        Array.Copy(udpHeader, 0, udpSegment, 0, 8);
        responseData.CopyTo(udpSegment.AsSpan(8));

        var udpChecksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 17, udpSegment);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(6, 2), udpChecksum);
        Array.Copy(udpHeader, 6, udpSegment, 6, 2);

        // --- Assemble ---
        var packet = new byte[totalLength];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(udpSegment, 0, packet, 20, udpSegment.Length);
        return packet;
    }

    /// <summary>
    /// 构建 IPv4 头部（20 字节，已计算校验和）
    /// </summary>
    private static byte[] BuildIPv4Header(byte[] sourceIP, byte[] destIP, byte protocol, ushort totalLength)
    {
        var ipHeader = new byte[20];
        ipHeader[0] = 0x45;                            // Version=4, IHL=5
        ipHeader[1] = 0x00;                            // DSCP/ECN
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), totalLength);
        // Identification (4-5) = 0
        ipHeader[6] = 0x40;                            // Flags: Don't Fragment
        ipHeader[7] = 0x00;                            // Fragment Offset
        ipHeader[8] = 0x40;                            // TTL = 64
        ipHeader[9] = protocol;
        // Checksum (10-11) = 0 (calculated below)
        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);

        var checksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), checksum);

        return ipHeader;
    }
}
