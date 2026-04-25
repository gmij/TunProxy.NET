using TunProxy.CLI;
using TunProxy.Core.Metrics;

namespace TunProxy.Tests;

public class TunTrafficLogSnapshotTests
{
    [Fact]
    public void Create_CapturesTrafficConnectionRelayAndDnsCounts()
    {
        var metrics = new ProxyMetrics();
        metrics.AddBytesSent(120);
        metrics.AddBytesReceived(240);

        var dns = new DnsDiagnosticsSnapshot
        {
            TcpSuccesses = 3,
            TcpQueries = 5,
            DohSuccesses = 7,
            DohQueries = 11
        };

        var snapshot = TunTrafficLogSnapshot.Create(
            metrics,
            proxyActiveConnections: 2,
            directActiveConnections: 4,
            relayStateCount: 6,
            dnsDiagnostics: dns,
            lastTcpConnectFailure: "connect timeout");

        Assert.Equal(120, snapshot.TotalBytesSent);
        Assert.Equal(240, snapshot.TotalBytesReceived);
        Assert.Equal(6, snapshot.ActiveConnections);
        Assert.Equal(6, snapshot.RelayStateCount);
        Assert.Equal(3, snapshot.DnsTcpSuccesses);
        Assert.Equal(5, snapshot.DnsTcpQueries);
        Assert.Equal(7, snapshot.DnsDohSuccesses);
        Assert.Equal(11, snapshot.DnsDohQueries);
        Assert.Equal("connect timeout", snapshot.LastTcpConnectFailure);
    }

    [Fact]
    public void Create_UsesDefaultsWhenDnsAndFailureAreMissing()
    {
        var snapshot = TunTrafficLogSnapshot.Create(
            new ProxyMetrics(),
            proxyActiveConnections: 0,
            directActiveConnections: 1,
            relayStateCount: 0,
            dnsDiagnostics: null,
            lastTcpConnectFailure: " ");

        Assert.Equal(1, snapshot.ActiveConnections);
        Assert.Equal(0, snapshot.DnsTcpSuccesses);
        Assert.Equal(0, snapshot.DnsTcpQueries);
        Assert.Equal(0, snapshot.DnsDohSuccesses);
        Assert.Equal(0, snapshot.DnsDohQueries);
        Assert.Equal(TunTrafficLogSnapshot.NoLastTcpConnectFailure, snapshot.LastTcpConnectFailure);
    }
}
