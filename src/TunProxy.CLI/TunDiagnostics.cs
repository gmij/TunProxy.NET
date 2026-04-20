using TunProxy.Core.Metrics;

namespace TunProxy.CLI;

public sealed class TunDiagnosticsSnapshot
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool IsRunning { get; set; }
    public string ProxyEndpoint { get; set; } = "";
    public string ProxyType { get; set; } = "";
    public string TunAddress { get; set; } = "";
    public string TunDnsServer { get; set; } = "";
    public string RouteMode { get; set; } = "";
    public bool EnableGeo { get; set; }
    public bool EnableGfwList { get; set; }
    public bool AutoAddDefaultRoute { get; set; }
    public string? OutboundBindAddress { get; set; }
    public int PacketWorkerCount { get; set; }
    public int PacketQueueDepth { get; set; }
    public int PacketQueueCapacity { get; set; }
    public int PendingConnectCount { get; set; }
    public int RelayStateCount { get; set; }
    public MetricsSnapshot Metrics { get; set; } = new();
    public DnsDiagnosticsSnapshot Dns { get; set; } = new();
    public RouteDiagnosticsSnapshot Route { get; set; } = new();
    public string? LastTcpConnectFailure { get; set; }
    public DateTime? LastTcpConnectFailureUtc { get; set; }
    public DateTime? LastPacketReadUtc { get; set; }
    public DateTime? LastPacketProcessedUtc { get; set; }
}

public sealed class DnsDiagnosticsSnapshot
{
    public long TcpQueries { get; set; }
    public long TcpSuccesses { get; set; }
    public long TcpFailures { get; set; }
    public long DohQueries { get; set; }
    public long DohSuccesses { get; set; }
    public long DohFailures { get; set; }
    public string? LastDomain { get; set; }
    public string? LastMethod { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastQueryUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
}

public sealed class RouteDiagnosticsSnapshot
{
    public string? OriginalDefaultGateway { get; set; }
    public bool? HasTunDefaultRoute { get; set; }
    public string? TunDefaultMetric { get; set; }
    public List<string> ProxyBypassRoutes { get; set; } = new();
    public List<string> Issues { get; set; } = new();
}
