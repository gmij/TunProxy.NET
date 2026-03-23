namespace TunProxy.Core.Packets;

/// <summary>
/// 协议检查器 - 从 TLS/HTTP 流量中提取域名信息
/// 从 TunProxyService 中提取，符合 SRP 原则
/// </summary>
public static class ProtocolInspector
{
    /// <summary>
    /// 从 TLS ClientHello 提取 SNI 域名
    /// </summary>
    public static string? ExtractSni(byte[] data)
    {
        try
        {
            // TLS Record: [0]=0x16(Handshake) [1-2]=version [3-4]=len
            if (data.Length < 9 || data[0] != 0x16) return null;
            // Handshake: [5]=0x01(ClientHello) [6-8]=len
            if (data[5] != 0x01) return null;

            int pos = 9; // ClientHello body starts here
            pos += 2;    // ClientVersion
            pos += 32;   // Random
            if (pos >= data.Length) return null;

            // Session ID
            pos += 1 + data[pos];
            if (pos + 2 > data.Length) return null;

            // Cipher Suites
            int csLen = (data[pos] << 8) | data[pos + 1];
            pos += 2 + csLen;
            if (pos + 1 > data.Length) return null;

            // Compression Methods
            pos += 1 + data[pos];
            if (pos + 2 > data.Length) return null;

            // Extensions
            int extEnd = pos + 2 + ((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            while (pos + 4 <= extEnd && pos + 4 <= data.Length)
            {
                int extType = (data[pos] << 8) | data[pos + 1];
                int extLen  = (data[pos + 2] << 8) | data[pos + 3];
                pos += 4;
                if (extType == 0x0000 && pos + 5 <= data.Length) // SNI
                {
                    // list_len(2) + name_type(1) + name_len(2) + name
                    int nameLen = (data[pos + 3] << 8) | data[pos + 4];
                    if (pos + 5 + nameLen <= data.Length)
                        return System.Text.Encoding.ASCII.GetString(data, pos + 5, nameLen);
                    return null;
                }
                pos += extLen;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 从 HTTP 请求头提取 Host 域名（不含端口）
    /// </summary>
    public static string? ExtractHttpHost(byte[] data)
    {
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(data);
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = line[5..].Trim().TrimEnd('\r');
                    // 去掉端口号
                    var colon = host.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(host[(colon + 1)..], out _))
                        host = host[..colon];
                    return host;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 判断是否为 RFC1918 私有地址或回环/本地链路地址
    /// </summary>
    public static bool IsPrivateIp(System.Net.IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 127                              // 127.0.0.0/8 回环
            || b[0] == 10                               // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)            // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254);           // 169.254.0.0/16 本地链路
    }

    /// <summary>
    /// 取 IPv4 的 /24 子网地址（如 106.11.43.246 → 106.11.43.0）
    /// </summary>
    public static string GetNet24(string ip)
    {
        var p = ip.Split('.');
        return p.Length == 4 ? $"{p[0]}.{p[1]}.{p[2]}.0" : ip;
    }

    /// <summary>
    /// 生成连接唯一键（统一 TunProxyService 和 TcpConnectionManager 的 MakeConnKey）
    /// </summary>
    public static string MakeConnectionKey(IPPacket packet) =>
        $"{packet.Header.SourceAddress}:{packet.SourcePort}-{packet.Header.DestinationAddress}:{packet.DestinationPort}";

    /// <summary>
    /// TCP 序列号比较（含回绕）：seq 是否 &lt;= boundary
    /// </summary>
    public static bool IsSeqBeforeOrEqual(uint seq, uint boundary) =>
        (int)(boundary - seq) >= 0;
}
