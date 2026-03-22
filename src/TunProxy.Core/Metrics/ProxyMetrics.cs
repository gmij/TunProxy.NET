namespace TunProxy.Core.Metrics;

/// <summary>
/// 代理统计指标
/// </summary>
public class ProxyMetrics
{
    private long _totalPackets;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _activeConnections;
    private long _totalConnections;
    private long _failedConnections;
    private long _dnsQueries;
    private long _failedDnsQueries;

    // 诊断统计 - 用于调试数据包过滤
    private long _rawPacketsReceived;  // 从 TUN 接收的原始数据包总数
    private long _parseFailures;        // 解析失败的数据包
    private long _ipv6Packets;          // IPv6 数据包（暂不支持）
    private long _nonTcpUdpPackets;     // 非 TCP/UDP 数据包（如 ICMP）
    private long _portFilteredPackets;  // 被端口过滤的数据包（非 80/443）
    private long _directRoutedPackets;  // 直连路由的数据包

    private readonly DateTime _startTime;

    public ProxyMetrics()
    {
        _startTime = DateTime.UtcNow;
    }

    // 数据包统计
    public long TotalPackets => Interlocked.Read(ref _totalPackets);
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    // 连接统计
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long FailedConnections => Interlocked.Read(ref _failedConnections);

    // DNS 统计
    public long DnsQueries => Interlocked.Read(ref _dnsQueries);
    public long FailedDnsQueries => Interlocked.Read(ref _failedDnsQueries);

    // 诊断统计
    public long RawPacketsReceived => Interlocked.Read(ref _rawPacketsReceived);
    public long ParseFailures => Interlocked.Read(ref _parseFailures);
    public long IPv6Packets => Interlocked.Read(ref _ipv6Packets);
    public long NonTcpUdpPackets => Interlocked.Read(ref _nonTcpUdpPackets);
    public long PortFilteredPackets => Interlocked.Read(ref _portFilteredPackets);
    public long DirectRoutedPackets => Interlocked.Read(ref _directRoutedPackets);

    // 运行时间
    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    // 计算速率
    public double BytesPerSecond => TotalBytesSent / Math.Max(1, Uptime.TotalSeconds);
    public double PacketsPerSecond => TotalPackets / Math.Max(1, Uptime.TotalSeconds);

    // 增量方法
    public void IncrementPackets() => Interlocked.Increment(ref _totalPackets);
    public void AddBytesSent(long bytes) => Interlocked.Add(ref _totalBytesSent, bytes);
    public void AddBytesReceived(long bytes) => Interlocked.Add(ref _totalBytesReceived, bytes);

    public void IncrementActiveConnections() => Interlocked.Increment(ref _activeConnections);
    public void DecrementActiveConnections() => Interlocked.Decrement(ref _activeConnections);
    public void IncrementTotalConnections() => Interlocked.Increment(ref _totalConnections);
    public void IncrementFailedConnections() => Interlocked.Increment(ref _failedConnections);

    public void IncrementDnsQueries() => Interlocked.Increment(ref _dnsQueries);
    public void IncrementFailedDnsQueries() => Interlocked.Increment(ref _failedDnsQueries);

    // 诊断方法
    public void IncrementRawPacketsReceived() => Interlocked.Increment(ref _rawPacketsReceived);
    public void IncrementParseFailures() => Interlocked.Increment(ref _parseFailures);
    public void IncrementIPv6Packets() => Interlocked.Increment(ref _ipv6Packets);
    public void IncrementNonTcpUdpPackets() => Interlocked.Increment(ref _nonTcpUdpPackets);
    public void IncrementPortFilteredPackets() => Interlocked.Increment(ref _portFilteredPackets);
    public void IncrementDirectRoutedPackets() => Interlocked.Increment(ref _directRoutedPackets);

    /// <summary>
    /// 获取指标快照（用于 API 返回）
    /// </summary>
    public MetricsSnapshot GetSnapshot()
    {
        return new MetricsSnapshot
        {
            TotalPackets = TotalPackets,
            TotalBytesSent = TotalBytesSent,
            TotalBytesReceived = TotalBytesReceived,
            ActiveConnections = ActiveConnections,
            TotalConnections = TotalConnections,
            FailedConnections = FailedConnections,
            DnsQueries = DnsQueries,
            FailedDnsQueries = FailedDnsQueries,
            RawPacketsReceived = RawPacketsReceived,
            ParseFailures = ParseFailures,
            IPv6Packets = IPv6Packets,
            NonTcpUdpPackets = NonTcpUdpPackets,
            PortFilteredPackets = PortFilteredPackets,
            DirectRoutedPackets = DirectRoutedPackets,
            UptimeSeconds = (long)Uptime.TotalSeconds,
            BytesPerSecond = BytesPerSecond,
            PacketsPerSecond = PacketsPerSecond
        };
    }
}

/// <summary>
/// 指标快照（用于序列化）
/// </summary>
public class MetricsSnapshot
{
    public long TotalPackets { get; set; }
    public long TotalBytesSent { get; set; }
    public long TotalBytesReceived { get; set; }
    public long ActiveConnections { get; set; }
    public long TotalConnections { get; set; }
    public long FailedConnections { get; set; }
    public long DnsQueries { get; set; }
    public long FailedDnsQueries { get; set; }
    public long RawPacketsReceived { get; set; }
    public long ParseFailures { get; set; }
    public long IPv6Packets { get; set; }
    public long NonTcpUdpPackets { get; set; }
    public long PortFilteredPackets { get; set; }
    public long DirectRoutedPackets { get; set; }
    public long UptimeSeconds { get; set; }
    public double BytesPerSecond { get; set; }
    public double PacketsPerSecond { get; set; }
}
