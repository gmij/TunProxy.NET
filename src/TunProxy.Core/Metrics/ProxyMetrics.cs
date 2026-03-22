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
    public long UptimeSeconds { get; set; }
    public double BytesPerSecond { get; set; }
    public double PacketsPerSecond { get; set; }
}
