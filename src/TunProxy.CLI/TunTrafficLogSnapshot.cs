using TunProxy.Core.Metrics;

namespace TunProxy.CLI;

internal readonly record struct TunTrafficLogSnapshot(
    long TotalBytesSent,
    long TotalBytesReceived,
    int ActiveConnections,
    int RelayStateCount,
    long DnsTcpSuccesses,
    long DnsTcpQueries,
    long DnsDohSuccesses,
    long DnsDohQueries,
    string LastTcpConnectFailure)
{
    public const string NoLastTcpConnectFailure = "(none)";

    public static TunTrafficLogSnapshot Create(
        ProxyMetrics metrics,
        int proxyActiveConnections,
        int directActiveConnections,
        int relayStateCount,
        DnsDiagnosticsSnapshot? dnsDiagnostics,
        string? lastTcpConnectFailure)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        dnsDiagnostics ??= new DnsDiagnosticsSnapshot();

        return new TunTrafficLogSnapshot(
            metrics.TotalBytesSent,
            metrics.TotalBytesReceived,
            proxyActiveConnections + directActiveConnections,
            relayStateCount,
            dnsDiagnostics.TcpSuccesses,
            dnsDiagnostics.TcpQueries,
            dnsDiagnostics.DohSuccesses,
            dnsDiagnostics.DohQueries,
            string.IsNullOrWhiteSpace(lastTcpConnectFailure)
                ? NoLastTcpConnectFailure
                : lastTcpConnectFailure);
    }
}
