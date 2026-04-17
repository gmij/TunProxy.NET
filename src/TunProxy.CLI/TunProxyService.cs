using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Connections;
using TunProxy.Core.Metrics;
using TunProxy.Core.Packets;
using TunProxy.Core.Route;
using TunProxy.Core.Tun;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// Main TUN proxy service coordinator.
/// </summary>
public class TunProxyService : IProxyService
{
    private readonly AppConfig _config;
    private ITunDevice? _tunDevice;
    private TcpConnectionManager? _connectionManager;
    private TcpConnectionManager? _directConnectionManager;
    private GeoIpService? _geoIpService;
    private GfwListService? _gfwListService;
    private IRouteService? _routeService;
    private IpCacheManager? _ipCache;
    private DnsProxyService? _dnsProxy;
    private CancellationTokenSource? _cts;
    private readonly ProxyMetrics _metrics = new();

    private readonly ConcurrentDictionary<string, TcpRelayState> _relayStates = new();
    private readonly TaskCompletionSource _proxyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile int _downloadingCount;
    private AsyncBoundedWorkQueue<byte[]>? _packetQueue;

    private const int PacketQueueCapacity = 4096;

    public TunProxyService(AppConfig config)
    {
        _config = config;

        if (config.Route.EnableGeo)
        {
            _geoIpService = new GeoIpService(config.Route.GeoIpDbPath);
        }

        if (config.Route.EnableGfwList)
        {
            _gfwListService = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
        }

        _routeService = RouteServiceFactory.Create(config.Tun);
        _ipCache = new IpCacheManager(_routeService);
        _dnsProxy = new DnsProxyService(
            config.Proxy.Host,
            config.Proxy.Port,
            config.Proxy.GetProxyType(),
            _ipCache,
            config.Tun.DnsServer,
            config.Proxy.Username,
            config.Proxy.Password);
    }

    public ServiceStatus GetStatus() => new()
    {
        Mode = "tun",
        IsRunning = _cts != null && !_cts.IsCancellationRequested,
        IsDownloading = _downloadingCount > 0,
        ProxyHost = _config.Proxy.Host,
        ProxyPort = _config.Proxy.Port,
        ProxyType = _config.Proxy.Type,
        ActiveConnections = (_connectionManager?.ActiveConnections ?? 0) + (_directConnectionManager?.ActiveConnections ?? 0),
        Metrics = _metrics.GetSnapshot()
    };

    public IReadOnlyDictionary<string, string> GetDnsCache() =>
        _ipCache?.GetHostnameCacheSnapshot() ?? new Dictionary<string, string>();

    public IReadOnlyList<string> GetDirectIps() =>
        _ipCache?.GetDirectIpSnapshot() ?? [];

    public async Task StartAsync(CancellationToken ct)
    {
        Log.Information("Starting TunProxy...");
        Log.Information("Proxy: {Host}:{Port} ({Type})", _config.Proxy.Host, _config.Proxy.Port, _config.Proxy.Type);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await EnsureWintunDllAsync(ct);
            }

            _tunDevice = TunDeviceFactory.Create(_config.Tun);
            _tunDevice.Start();
            _tunDevice.Configure(_config.Tun.IpAddress, _config.Tun.SubnetMask);

            IPAddress? proxyBindAddr = ProbeLocalIpForHost(_config.Proxy.Host, _config.Proxy.Port);

            if (_config.Route.AutoAddDefaultRoute)
            {
                _routeService!.AddBypassRoute(_config.Proxy.Host);
                _ipCache!.LoadAndApplyDirectIpCache();
                _ipCache.LoadBlockedIpCache();
                _routeService.AddDefaultRoute();
            }

            _connectionManager = new TcpConnectionManager(
                _config.Proxy.Host,
                _config.Proxy.Port,
                _config.Proxy.GetProxyType(),
                _config.Proxy.Username,
                _config.Proxy.Password,
                bindAddress: proxyBindAddr);
            _directConnectionManager = new TcpConnectionManager(string.Empty, 0, ProxyType.Direct);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            StartPacketWorkers(_cts.Token);
            InitializeBackgroundServices();
            StartMetricsLogger(_cts.Token);

            Log.Information("TunProxy is running. Press Ctrl+C to stop.");
            await Task.Run(() => PacketLoopAsync(_tunDevice, _cts.Token), _cts.Token);
        }
        finally
        {
            await StopAsync();
        }
    }

    public async Task StopAsync()
    {
        Log.Information("Stopping service...");
        try
        {
            _cts?.Cancel();
            _packetQueue?.Complete();
            if (_packetQueue != null)
            {
                await Task.WhenAny(_packetQueue.Completion, Task.Delay(2000));
            }
            await Task.Delay(1000);

            _connectionManager?.Dispose();
            _directConnectionManager?.Dispose();

            _routeService?.RemoveDefaultRoute();
            _routeService?.ClearAllBypassRoutes();

            _tunDevice?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stopping service");
        }
    }

    private void StartPacketWorkers(CancellationToken ct)
    {
        var workerCount = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
        _packetQueue = new AsyncBoundedWorkQueue<byte[]>(
            PacketQueueCapacity,
            workerCount,
            ProcessPacketAsync);
        _packetQueue.Start(ct);

        Log.Information(
            "Packet processing queue started with {Workers} workers and capacity {Capacity}",
            workerCount,
            PacketQueueCapacity);
    }

    private async Task PacketLoopAsync(ITunDevice device, CancellationToken ct)
    {
        _proxyReady.TrySetResult();

        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
            {
                _connectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(2));
                _directConnectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(2));
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            var data = device.ReadPacket();
            if (data == null)
            {
                break;
            }

            _metrics.IncrementRawPacketsReceived();
            if (_packetQueue == null)
            {
                await ProcessPacketAsync(data, ct);
                continue;
            }

            await _packetQueue.EnqueueAsync(data, ct);
        }
    }

    private async Task ProcessPacketAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            var device = _tunDevice;
            if (device == null)
            {
                return;
            }

            var packet = IPPacket.Parse(data);
            if (packet == null)
            {
                _metrics.IncrementParseFailures();
                return;
            }

            _metrics.IncrementPackets();

            if (packet.IsUDP && packet.DestinationPort == 53)
            {
                _metrics.IncrementDnsQueries();
                await _dnsProxy!.ProcessDnsQueryAsync(device, packet, ct);
                return;
            }

            if (packet.IsUDP)
            {
                _metrics.IncrementNonTcpUdpPackets();
                TunWriter.WriteIcmpPortUnreachable(device, packet);
                return;
            }

            if (!packet.IsTCP || packet.SourcePort == null || packet.DestinationPort == null)
            {
                _metrics.IncrementNonTcpUdpPackets();
                return;
            }

            var tcpFlags = packet.TCPHeader!.Value;
            var destPort = packet.DestinationPort.Value;
            var destIP = packet.Header.DestinationAddress.ToString();

            if (packet.Payload.Length == 0 && !tcpFlags.SYN && !tcpFlags.FIN && !tcpFlags.RST)
            {
                return;
            }

            if (ProtocolInspector.IsPrivateIp(packet.Header.DestinationAddress))
            {
                if (tcpFlags.SYN)
                {
                    TunWriter.WriteRst(device, packet);
                }
                return;
            }

            var shouldProxy = true;
            if (_config.Route.EnableGeo && _geoIpService != null)
            {
                shouldProxy = _geoIpService.ShouldProxy(
                    packet.Header.DestinationAddress,
                    _config.Route.GeoProxy,
                    _config.Route.GeoDirect);
            }

            if (!shouldProxy)
            {
                _metrics.IncrementDirectRoutedPackets();
            }

            var connManager = shouldProxy ? _connectionManager! : _directConnectionManager!;
            var connKey = ProtocolInspector.MakeConnectionKey(packet);

            if (tcpFlags.SYN && !tcpFlags.ACK)
            {
                if (!shouldProxy && _routeService != null)
                {
                    Log.Debug("[CONN] {DestIP}:{Port}  DIRECT/RST  (GEO)", destIP, destPort);
                    _ipCache!.TryAddDirectBypass(ProtocolInspector.GetNet24(destIP));
                    TunWriter.WriteRst(device, packet);
                    return;
                }

                if (_ipCache!.IsProxyBlocked(destIP) || _ipCache.IsConnectFailed(destIP))
                {
                    TunWriter.WriteRst(device, packet);
                    return;
                }

                TunWriter.WriteSynAck(device, packet, out var serverIsn);
                _relayStates[connKey] = new TcpRelayState(packet, serverIsn, tcpFlags.SequenceNumber);
                return;
            }

            if (!_relayStates.TryGetValue(connKey, out var state))
            {
                if (packet.Payload.Length > 0)
                {
                    TunWriter.WriteRst(device, packet);
                }
                else
                {
                    connManager.RemoveConnectionByKey(connKey);
                }

                return;
            }

            if (tcpFlags.FIN || tcpFlags.RST)
            {
                if (_relayStates.TryRemove(connKey, out _))
                {
                    (state.ConnectionManager ?? connManager).RemoveConnectionByKey(connKey);
                    if (state.IsProxyConnected)
                    {
                        _metrics.DecrementActiveConnections();
                    }
                }
                return;
            }

            if (packet.Payload.Length > 0)
            {
                TcpConnection? conn;
                if (!state.IsProxyConnected)
                {
                    if (Interlocked.CompareExchange(ref state.ConnectStarted, 1, 0) != 0)
                    {
                        return;
                    }

                    _metrics.IncrementTotalConnections();
                    _metrics.IncrementActiveConnections();

                    string connectHost;
                    string domainSource;

                    if (destPort == 443)
                    {
                        var sni = ProtocolInspector.ExtractSni(packet.Payload);
                        var cached = _ipCache!.GetCachedHostname(destIP);
                        connectHost = sni ?? cached ?? destIP;
                        domainSource = sni != null ? "SNI" : cached != null ? "DNS" : "IP";
                    }
                    else if (destPort == 80)
                    {
                        var host = ProtocolInspector.ExtractHttpHost(packet.Payload);
                        var cached = _ipCache!.GetCachedHostname(destIP);
                        connectHost = host ?? cached ?? destIP;
                        domainSource = host != null ? "Host" : cached != null ? "DNS" : "IP";
                    }
                    else
                    {
                        var cached = _ipCache!.GetCachedHostname(destIP);
                        connectHost = cached ?? destIP;
                        domainSource = cached != null ? "DNS" : "IP";
                    }

                    var gfwOverride = false;
                    if (_config.Route.EnableGfwList && _gfwListService != null && connectHost != destIP)
                    {
                        var gfwProxy = _gfwListService.IsInGfwList(connectHost);
                        if (gfwProxy != shouldProxy)
                        {
                            connManager = gfwProxy ? _connectionManager! : _directConnectionManager!;
                            gfwOverride = true;
                        }
                    }

                    var usingProxy = connManager == _connectionManager;
                    var routeLabel = usingProxy ? "PROXY" : "DIRECT";
                    var srcLabel = gfwOverride ? domainSource + "/GFW" : domainSource;
                    Log.Information("[CONN] {Host}:{Port}  {Route}  ({Source})", connectHost, destPort, routeLabel, srcLabel);

                    conn = connManager.GetOrCreateConnection(packet);
                    if (conn == null)
                    {
                        HandleConnectionFail(connKey, device, packet);
                        return;
                    }

                    try
                    {
                        await conn.ConnectAsync(connectHost, destPort, ct);
                        state.IsProxyConnected = true;
                        state.ConnectionManager = connManager;
                        _ = Task.Run(() => RelayProxyToTunAsync(device, conn, connKey, connManager, ct), ct);
                    }
                    catch (Exception ex)
                    {
                        var reason = ex.Message.Contains("PROXY_DENIED") ? "proxy denied"
                                   : ex.Message.Contains("CONNECT_FAILED") ? "connect failed"
                                   : "error";
                        Log.Warning("[CONN] {Host}:{Port}  dropped  ({Reason})", connectHost, destPort, reason);

                        if (ex.Message.Contains("PROXY_DENIED"))
                        {
                            _ipCache!.TryAddProxyBlocked(destIP);
                        }
                        else if (ex.Message.Contains("CONNECT_FAILED"))
                        {
                            _ipCache!.RecordConnectFailed(destIP);
                        }

                        HandleConnectionFail(connKey, device, packet);
                        return;
                    }
                }
                else
                {
                    conn = (state.ConnectionManager ?? connManager).GetExistingConnection(packet);
                    if (conn == null)
                    {
                        return;
                    }
                }

                var incomingEnd = tcpFlags.SequenceNumber + (uint)packet.Payload.Length;
                if (ProtocolInspector.IsSeqBeforeOrEqual(incomingEnd, state.ExpectedClientSeq))
                {
                    TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
                    return;
                }

                state.ExpectedClientSeq = incomingEnd;
                try
                {
                    await conn.SendAsync(packet.Payload, ct);
                    _metrics.AddBytesSent(packet.Payload.Length);
                    TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
                }
                catch
                {
                    HandleConnectionFail(connKey, device, packet);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Packet processing error");
        }
    }

    private void HandleConnectionFail(string connKey, ITunDevice device, IPPacket packet)
    {
        _metrics.DecrementActiveConnections();
        _metrics.IncrementFailedConnections();
        if (_relayStates.TryRemove(connKey, out var state))
        {
            (state.ConnectionManager ?? _connectionManager)?.RemoveConnectionByKey(connKey);
        }

        TunWriter.WriteRst(device, packet);
    }

    private async Task RelayProxyToTunAsync(
        ITunDevice device,
        TcpConnection connection,
        string connKey,
        TcpConnectionManager connManager,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        var normalClose = false;
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                int bytesRead;
                try
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromMinutes(2));
                    bytesRead = await connection.ReceiveAsync(buffer, readCts.Token);
                }
                catch
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    normalClose = true;
                    break;
                }

                if (!_relayStates.TryGetValue(connKey, out var state))
                {
                    break;
                }

                _metrics.AddBytesReceived(bytesRead);

                const int Mss = 1460;
                var offset = 0;
                while (offset < bytesRead)
                {
                    var segmentLength = Math.Min(Mss, bytesRead - offset);
                    TunWriter.WriteDataResponse(
                        device,
                        state.SynPacket,
                        buffer.AsSpan(offset, segmentLength),
                        state.NextServerSeq,
                        state.ExpectedClientSeq);
                    state.NextServerSeq += (uint)segmentLength;
                    offset += segmentLength;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (normalClose && _relayStates.TryGetValue(connKey, out var state))
            {
                TunWriter.WriteFinAck(device, state.SynPacket, state.NextServerSeq, state.ExpectedClientSeq);
            }

            if (_relayStates.TryRemove(connKey, out _))
            {
                connManager.RemoveConnectionByKey(connKey);
                _metrics.DecrementActiveConnections();
            }
        }
    }

    private void InitializeBackgroundServices()
    {
        string? httpUrl = _config.Proxy.GetProxyType() == ProxyType.Http
            ? $"http://{_config.Proxy.Host}:{_config.Proxy.Port}"
            : null;

        if (_config.Route.EnableGeo && _geoIpService != null)
        {
            _ = Task.Run(async () =>
            {
                if (httpUrl == null)
                {
                    await _proxyReady.Task;
                }

                Interlocked.Increment(ref _downloadingCount);
                try
                {
                    await _geoIpService.InitializeAsync(_cts!.Token, httpUrl);
                }
                finally
                {
                    Interlocked.Decrement(ref _downloadingCount);
                }
            });
        }

        if (_config.Route.EnableGfwList && _gfwListService != null)
        {
            _ = Task.Run(async () =>
            {
                if (httpUrl == null)
                {
                    await _proxyReady.Task;
                }

                Interlocked.Increment(ref _downloadingCount);
                try
                {
                    await _gfwListService.InitializeAsync(_cts!.Token, httpUrl);
                }
                finally
                {
                    Interlocked.Decrement(ref _downloadingCount);
                }
            });
        }
    }

    private void StartMetricsLogger(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(ct))
            {
                Log.Information(
                    "Traffic: sent {Sent} B, received {Received} B, active {Active}",
                    _metrics.TotalBytesSent,
                    _metrics.TotalBytesReceived,
                    (_connectionManager?.ActiveConnections ?? 0) + (_directConnectionManager?.ActiveConnections ?? 0));
            }
        }, ct);
    }

    private static IPAddress? ProbeLocalIpForHost(string host, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, port);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return null;
        }
    }

    private void AddDnsServersBypassRoutes()
    {
        if (_routeService == null)
        {
            return;
        }

        var dnsServers = new HashSet<string> { "8.8.8.8", "8.8.4.4", "1.1.1.1", "114.114.114.114", "223.5.5.5" };
        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }

            foreach (var dns in networkInterface.GetIPProperties().DnsAddresses)
            {
                if (dns.AddressFamily == AddressFamily.InterNetwork && !dns.ToString().StartsWith("127.", StringComparison.Ordinal))
                {
                    dnsServers.Add(dns.ToString());
                }
            }
        }

        foreach (var dns in dnsServers)
        {
            _routeService.AddBypassRoute(dns);
        }
    }

    private static async Task EnsureWintunDllAsync(CancellationToken ct)
    {
        var wintunPath = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
        if (File.Exists(wintunPath))
        {
            return;
        }

        var url = Environment.Is64BitProcess
            ? "https://git.zx2c4.com/wintun/plain/bin/amd64/wintun.dll"
            : "https://git.zx2c4.com/wintun/plain/bin/x86/wintun.dll";

        try
        {
            using var client = new HttpClient();
            var data = await client.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(wintunPath, data, ct);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to download Wintun.dll automatically: {Message}", ex.Message);
        }
    }
}

internal class TcpRelayState
{
    public uint NextServerSeq;
    public uint ExpectedClientSeq;
    public readonly IPPacket SynPacket;
    public bool IsProxyConnected;
    public int ConnectStarted;
    public TcpConnectionManager? ConnectionManager;

    public TcpRelayState(IPPacket synPacket, uint serverIsn, uint clientIsn)
    {
        SynPacket = synPacket;
        NextServerSeq = serverIsn + 1;
        ExpectedClientSeq = clientIsn + 1;
    }
}
