using System.Collections.Concurrent;
using System.Net;
using TunProxy.Core.Configuration;
using TunProxy.Core.Connections;
using TunProxy.Core.Metrics;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal static class TunRuntimeDiagnosticsProvider
{
    public static ServiceStatus CreateStatus(
        AppConfig config,
        CancellationTokenSource? cancellation,
        int downloadingCount,
        TcpConnectionManager? proxyConnections,
        TcpConnectionManager? directConnections,
        ProxyMetrics metrics)
    {
        return new ServiceStatus
        {
            Mode = "tun",
            IsRunning = cancellation != null && !cancellation.IsCancellationRequested,
            IsDownloading = downloadingCount > 0,
            ProxyHost = config.Proxy.Host,
            ProxyPort = config.Proxy.Port,
            ProxyType = config.Proxy.Type,
            ActiveConnections = GetActiveConnections(proxyConnections, directConnections),
            Metrics = metrics.GetSnapshot()
        };
    }

    public static TunDiagnosticsSnapshot CreateDiagnostics(
        AppConfig config,
        CancellationTokenSource? cancellation,
        IPAddress? outboundBindAddress,
        TunPacketPipeline? packetPipeline,
        ConcurrentDictionary<string, TcpRelayState> relayStates,
        ProxyMetrics metrics,
        IRouteService? routeService,
        IReadOnlyCollection<string> proxyBypassRoutes,
        DirectBypassRouteManager directBypassRoutes,
        DnsProxyService? dnsProxy,
        string? lastTcpConnectFailure,
        DateTime? lastTcpConnectFailureUtc,
        DateTime? lastPacketReadUtc,
        DateTime? lastPacketProcessedUtc)
    {
        var route = TunRouteDiagnosticsBuilder.Build(
            routeService,
            config.Tun.IpAddress,
            proxyBypassRoutes,
            directBypassRoutes.GetSnapshot(),
            directBypassRoutes.Count);

        return new TunDiagnosticsSnapshot
        {
            IsRunning = cancellation != null && !cancellation.IsCancellationRequested,
            ProxyEndpoint = $"{config.Proxy.Host}:{config.Proxy.Port}",
            ProxyType = config.Proxy.Type,
            TunAddress = $"{config.Tun.IpAddress}/{config.Tun.SubnetMask}",
            TunDnsServer = config.Tun.DnsServer,
            RouteMode = config.Route.Mode,
            EnableGeo = config.Route.EnableGeo,
            EnableGfwList = config.Route.EnableGfwList,
            AutoAddDefaultRoute = config.Route.AutoAddDefaultRoute,
            OutboundBindAddress = outboundBindAddress?.ToString(),
            PacketWorkerCount = packetPipeline?.WorkerCount ?? 0,
            PacketQueueDepth = packetPipeline?.Count ?? 0,
            PacketQueueCapacity = packetPipeline?.Capacity ?? 0,
            PendingConnectCount = relayStates.Values.Count(static state =>
                Volatile.Read(ref state.ConnectStarted) != 0 &&
                !Volatile.Read(ref state.IsProxyConnected)),
            RelayStateCount = relayStates.Count,
            Metrics = metrics.GetSnapshot(),
            Dns = dnsProxy?.GetDiagnostics() ?? new DnsDiagnosticsSnapshot(),
            Route = route,
            LastTcpConnectFailure = lastTcpConnectFailure,
            LastTcpConnectFailureUtc = lastTcpConnectFailureUtc,
            LastPacketReadUtc = lastPacketReadUtc,
            LastPacketProcessedUtc = lastPacketProcessedUtc
        };
    }

    private static int GetActiveConnections(
        TcpConnectionManager? proxyConnections,
        TcpConnectionManager? directConnections)
    {
        return (proxyConnections?.ActiveConnections ?? 0) + (directConnections?.ActiveConnections ?? 0);
    }
}
