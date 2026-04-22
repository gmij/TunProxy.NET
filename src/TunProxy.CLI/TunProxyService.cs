using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
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
    private readonly GeoIpService? _geoIpService;
    private readonly GfwListService? _gfwListService;
    private readonly RouteDecisionService _routeDecision;
    private readonly IRouteService? _routeService;
    private readonly IpCacheManager? _ipCache;
    private readonly DnsResolutionStore? _dnsStore;
    private readonly DnsProxyService? _dnsProxy;
    private CancellationTokenSource? _cts;
    private readonly ProxyMetrics _metrics = new();

    private readonly ConcurrentDictionary<string, TcpRelayState> _relayStates = new();
    private readonly TaskCompletionSource _proxyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile int _downloadingCount;
    private AsyncBoundedWorkQueue<byte[]>? _packetQueue;
    private IPAddress? _outboundBindAddress;
    private int _packetWorkerCount;
    private readonly ConcurrentBag<string> _proxyBypassRoutes = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _directBypassRoutes = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastTcpConnectFailure;
    private DateTime? _lastTcpConnectFailureUtc;
    private DateTime? _lastPacketReadUtc;
    private DateTime? _lastPacketProcessedUtc;

    private const int PacketQueueCapacity = 4096;
    private const int MaxInitialPayloadBufferBytes = 64 * 1024;

    public TunProxyService(AppConfig config)
    {
        _config = config;

        _geoIpService = new GeoIpService(config.Route.GeoIpDbPath);
        _gfwListService = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);

        _routeDecision = new RouteDecisionService(config, _geoIpService, _gfwListService);
        _routeService = RouteServiceFactory.Create(config.Tun);
        _ipCache = new IpCacheManager(_routeService);
        _dnsStore = new DnsResolutionStore();
        _dnsProxy = new DnsProxyService(
            config.Proxy.Host,
            config.Proxy.Port,
            config.Proxy.GetProxyType(),
            _dnsStore,
            config.Tun.DnsServer,
            config.Proxy.Username,
            config.Proxy.Password,
            _routeDecision);
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

    public IReadOnlyList<string> GetDirectIps() =>
        _ipCache?.GetDirectIpSnapshot() ?? [];

    public async Task<IReadOnlyList<DnsRouteRecord>> GetDnsRouteRecordsAsync(CancellationToken ct)
    {
        var snapshots = _dnsStore?.GetResolutionSnapshot() ?? [];
        var directIps = new HashSet<string>(GetDirectIps(), StringComparer.OrdinalIgnoreCase);
        var records = new List<DnsRouteRecord>(snapshots.Count);

        foreach (var snapshot in snapshots)
        {
            if (!IPAddress.TryParse(snapshot.IpAddress, out var ipAddress))
            {
                continue;
            }

            var decision = await _routeDecision.DecideForObservedAddressAsync(snapshot.Hostname, ipAddress, ct);
            records.Add(new DnsRouteRecord(
                snapshot.IpAddress,
                snapshot.Hostname,
                decision.ShouldProxy ? "PROXY" : "DIRECT",
                decision.Reason,
                ProtocolInspector.IsPrivateIp(ipAddress),
                directIps.Contains(snapshot.IpAddress),
                snapshot.IsDnsCached,
                snapshot.DnsLastActiveUtc ?? snapshot.LastSeenUtc,
                snapshot.DnsExpiresUtc));
        }

        return records
            .OrderBy(static item => item.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Route)
            .ThenBy(static item => item.Reason, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool ClearDnsCacheEntry(string? hostname, string ipAddress) =>
        _dnsStore?.RemoveCachedAddress(hostname, ipAddress) == true;

    public async Task RefreshRuleResourcesAsync(CancellationToken ct)
    {
        if (!NeedsRuleResourceInitialization())
        {
            return;
        }

        await InitializeRuleServicesAsync(
            ct,
            _config.Proxy,
            waitForProxyReadyWhenNeeded: false,
            downloadIfMissing: false);
    }

    public TunDiagnosticsSnapshot GetDiagnostics()
    {
        var route = new RouteDiagnosticsSnapshot
        {
            ProxyBypassRoutes = _proxyBypassRoutes.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

        try
        {
            route.OriginalDefaultGateway = _routeService?.GetOriginalDefaultGateway();
            if (_routeService is WindowsRouteService windowsRouteService)
            {
                var routes = windowsRouteService.GetRouteTable();
                var tunDefault = windowsRouteService.GetTunDefaultRoute();

                route.HasTunDefaultRoute = tunDefault != null;
                route.TunDefaultMetric = tunDefault?.Metric;

                if (tunDefault == null)
                {
                    route.Issues.Add("TUN default route is missing.");
                }

                var competingRoutes = routes
                    .Where(r => r.Network == "0.0.0.0" &&
                                !WindowsRouteService.IsTunDefaultRoute(r, _config.Tun.IpAddress))
                    .ToList();
                if (tunDefault != null &&
                    int.TryParse(tunDefault.Metric, out var tunMetric) &&
                    competingRoutes.Any(r => int.TryParse(r.Metric, out var metric) && metric < tunMetric))
                {
                    route.Issues.Add($"Another default route has a higher priority than TUN metric {tunMetric}.");
                }
            }
        }
        catch (Exception ex)
        {
            route.Issues.Add($"Route diagnostics failed: {ex.GetType().Name}: {ex.Message}");
        }

        var dnsDiagnostics = _dnsProxy?.GetDiagnostics() ?? new DnsDiagnosticsSnapshot();

        return new TunDiagnosticsSnapshot
        {
            IsRunning = _cts != null && !_cts.IsCancellationRequested,
            ProxyEndpoint = $"{_config.Proxy.Host}:{_config.Proxy.Port}",
            ProxyType = _config.Proxy.Type,
            TunAddress = $"{_config.Tun.IpAddress}/{_config.Tun.SubnetMask}",
            TunDnsServer = _config.Tun.DnsServer,
            RouteMode = _config.Route.Mode,
            EnableGeo = _config.Route.EnableGeo,
            EnableGfwList = _config.Route.EnableGfwList,
            AutoAddDefaultRoute = _config.Route.AutoAddDefaultRoute,
            OutboundBindAddress = _outboundBindAddress?.ToString(),
            PacketWorkerCount = _packetWorkerCount,
            PacketQueueDepth = _packetQueue?.Count ?? 0,
            PacketQueueCapacity = PacketQueueCapacity,
            PendingConnectCount = _relayStates.Values.Count(static state =>
                Volatile.Read(ref state.ConnectStarted) != 0 &&
                !Volatile.Read(ref state.IsProxyConnected)),
            RelayStateCount = _relayStates.Count,
            Metrics = _metrics.GetSnapshot(),
            Dns = dnsDiagnostics,
            Route = route,
            LastTcpConnectFailure = _lastTcpConnectFailure,
            LastTcpConnectFailureUtc = _lastTcpConnectFailureUtc,
            LastPacketReadUtc = _lastPacketReadUtc,
            LastPacketProcessedUtc = _lastPacketProcessedUtc
        };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Log.Information("Starting TunProxy...");
        Log.Information("Proxy: {Host}:{Port} ({Type})", _config.Proxy.Host, _config.Proxy.Port, _config.Proxy.Type);

        try
        {
            if (NeedsRuleResourceInitialization())
            {
                Log.Information("[TUN ] Loading existing GFWList/GeoIP resources before applying TUN routes.");
                if (!await InitializeRuleServicesAsync(
                        ct,
                        _config.Proxy,
                        waitForProxyReadyWhenNeeded: false,
                        downloadIfMissing: false))
                {
                    Log.Warning("[TUN ] One or more enabled route rule resources are missing or invalid; those rules will stay inactive.");
                }
            }

            if (OperatingSystem.IsWindows())
            {
                Log.Information("[TUN ] Checking wintun.dll...");
                await EnsureWintunDllAsync(ct);
            }

            Log.Information("[TUN ] Creating TUN device...");
            _tunDevice = TunDeviceFactory.Create(_config.Tun);
            Log.Information("[TUN ] Starting TUN device...");
            _tunDevice.Start();
            Log.Information("[TUN ] Configuring TUN address {IP}/{Mask}...", _config.Tun.IpAddress, _config.Tun.SubnetMask);
            _tunDevice.Configure(_config.Tun.IpAddress, _config.Tun.SubnetMask);

            _outboundBindAddress = DeterminePreferredBindAddress();
            Log.Information(
                "[TUN ] Outbound bind address: {BindAddress}",
                _outboundBindAddress?.ToString() ?? "(not bound)");

            if (_config.Route.AutoAddDefaultRoute)
            {
                Log.Information("[ROUTE] Applying proxy bypass routes and TUN default route...");
                AddProxyBypassRoutes();
                _ipCache!.RetireDirectIpCache();
                _ipCache.LoadBlockedIpCache();
                var defaultRouteReady = _routeService!.AddDefaultRoute();
                Log.Information("[ROUTE] Route setup completed. DefaultRouteReady={DefaultRouteReady}", defaultRouteReady);
                if (!defaultRouteReady)
                {
                    Log.Warning("[ROUTE] TUN default route is still missing; system traffic may not enter TUN.");
                }
            }

            Log.Information("[TUN ] Creating connection managers...");
            _connectionManager = new TcpConnectionManager(
                _config.Proxy.Host,
                _config.Proxy.Port,
                _config.Proxy.GetProxyType(),
                _config.Proxy.Username,
                _config.Proxy.Password,
                connectionTimeout: TimeSpan.FromSeconds(8),
                bindAddress: _outboundBindAddress);
            _directConnectionManager = new TcpConnectionManager(
                string.Empty,
                0,
                ProxyType.Direct,
                connectionTimeout: TimeSpan.FromSeconds(8));

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Log.Information("[TUN ] Starting packet workers and background services...");
            InitializeBackgroundServices();
            StartPacketWorkers(_cts.Token);
            StartRuntimeCleanup(_cts.Token);
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
        _packetWorkerCount = workerCount;
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

    private void StartRuntimeCleanup(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    _connectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(2));
                    _directConnectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(2));
                    _dnsStore?.CleanupExpired();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }, ct);
    }

    private async Task PacketLoopAsync(ITunDevice device, CancellationToken ct)
    {
        _proxyReady.TrySetResult();

        while (!ct.IsCancellationRequested)
        {
            var data = device.ReadPacket();
            if (data == null)
            {
                break;
            }

            _metrics.IncrementRawPacketsReceived();
            _lastPacketReadUtc = DateTime.UtcNow;
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
                if (IsIpv6Packet(data))
                {
                    _metrics.IncrementIPv6Packets();
                }
                else
                {
                    _metrics.IncrementParseFailures();
                }

                return;
            }

            _metrics.IncrementPackets();
            _lastPacketProcessedUtc = DateTime.UtcNow;

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

            var initialDecision = await _routeDecision.DecideForTunAsync(null, packet.Header.DestinationAddress, ct);
            var shouldProxy = initialDecision.ShouldProxy;
            var connManager = shouldProxy ? _connectionManager! : _directConnectionManager!;
            var connKey = ProtocolInspector.MakeConnectionKey(packet);

            if (tcpFlags.SYN && !tcpFlags.ACK)
            {
                if (!shouldProxy)
                {
                    Log.Debug("[CONN] {DestIP}:{Port}  DIRECT  ({Reason})",
                        destIP,
                        destPort,
                        initialDecision.Reason);
                }

                if (initialDecision.ShouldProxy &&
                    (_ipCache!.IsProxyBlocked(destIP) || _ipCache.IsConnectFailed(destIP)))
                {
                    TunWriter.WriteRst(device, packet);
                    return;
                }

                if (_relayStates.TryGetValue(connKey, out var existingState))
                {
                    TunWriter.WriteSynAck(device, packet, existingState.ServerIsn);
                    return;
                }

                TunWriter.WriteSynAck(device, packet, out var serverIsn);
                var newState = new TcpRelayState(packet, serverIsn, tcpFlags.SequenceNumber);
                if (!_relayStates.TryAdd(connKey, newState) &&
                    _relayStates.TryGetValue(connKey, out existingState))
                {
                    TunWriter.WriteSynAck(device, packet, existingState.ServerIsn);
                }
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
                    var preConnect = await PreparePreConnectPayloadAsync(
                            device,
                            state,
                            packet,
                            destPort,
                            destIP,
                            ct);
                    if (!preConnect.ShouldStartConnect)
                    {
                        return;
                    }

                    var initialPayload = preConnect.PayloadForTarget;
                    var initialPayloadAlreadyAcked = preConnect.PayloadAlreadyAcked;

                    _metrics.IncrementTotalConnections();
                    _metrics.IncrementActiveConnections();

                    string connectHost;
                    string domainSource;

                    if (destPort == 443)
                    {
                        var sni = ProtocolInspector.ExtractSni(initialPayload);
                        var cached = _dnsStore!.GetCachedHostname(destIP);
                        connectHost = sni ?? cached ?? destIP;
                        domainSource = sni != null ? "SNI" : cached != null ? "DNS" : "IP";
                    }
                    else if (destPort == 80)
                    {
                        var host = ProtocolInspector.ExtractHttpHost(initialPayload);
                        var cached = _dnsStore!.GetCachedHostname(destIP);
                        connectHost = host ?? cached ?? destIP;
                        domainSource = host != null ? "Host" : cached != null ? "DNS" : "IP";
                    }
                    else
                    {
                        var cached = _dnsStore!.GetCachedHostname(destIP);
                        connectHost = cached ?? destIP;
                        domainSource = cached != null ? "DNS" : "IP";
                    }

                    var finalDecision = await _routeDecision.DecideForTunAsync(
                        connectHost != destIP ? connectHost : null,
                        packet.Header.DestinationAddress,
                        ct);
                    if (connectHost != destIP && !IPAddress.TryParse(connectHost, out _))
                    {
                        _dnsStore!.RecordObservedHostname(
                            destIP,
                            connectHost,
                            domainSource,
                            finalDecision.ShouldProxy ? "PROXY" : "DIRECT",
                            finalDecision.Reason);

                        if (finalDecision.EvaluatedIp != null &&
                            !finalDecision.EvaluatedIp.Equals(packet.Header.DestinationAddress))
                        {
                            _dnsStore!.RecordObservedHostname(
                                finalDecision.EvaluatedIp.ToString(),
                                connectHost,
                                domainSource,
                                finalDecision.ShouldProxy ? "PROXY" : "DIRECT",
                                finalDecision.Reason);
                        }
                    }

                    shouldProxy = finalDecision.ShouldProxy;
                    connManager = shouldProxy ? _connectionManager! : _directConnectionManager!;
                    if (!shouldProxy)
                    {
                        _metrics.IncrementDirectRoutedPackets();
                        var routeIp = finalDecision.EvaluatedIp ?? packet.Header.DestinationAddress;
                        await EnsureDirectBypassRouteAsync(routeIp.ToString(), routeIp, finalDecision);
                    }

                    var upstreamHost = shouldProxy
                        ? connectHost
                        : finalDecision.EvaluatedIp?.ToString() ?? (connectHost != destIP ? connectHost : destIP);
                    var dnsCacheHost = connectHost != destIP && !IPAddress.TryParse(connectHost, out _)
                        ? connectHost
                        : null;
                    var usingProxy = connManager == _connectionManager;
                    var routeLabel = usingProxy ? "PROXY" : "DIRECT";
                    var srcLabel = $"{domainSource}/{finalDecision.Reason}";
                    Log.Information("[CONN] {Host}:{Port}  {Route}  ({Source})", connectHost, destPort, routeLabel, srcLabel);

                    conn = connManager.GetOrCreateConnection(packet);
                    if (conn == null)
                    {
                        HandleConnectionFail(connKey, device, packet);
                        return;
                    }

                    StartInitialConnectAndSend(
                        device,
                        conn,
                        connKey,
                        connManager,
                        state,
                        packet,
                        upstreamHost,
                        destPort,
                        routeLabel,
                        srcLabel,
                        destIP,
                        dnsCacheHost,
                        initialPayload,
                        initialPayloadAlreadyAcked,
                        ct);
                    return;
                }
                else
                {
                    conn = (state.ConnectionManager ?? connManager).GetExistingConnection(packet);
                    if (conn == null)
                    {
                        return;
                    }
                }

                await SendClientPayloadAsync(device, conn, state, packet, connKey, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Packet processing error");
        }
    }

    private async Task<PreConnectPayloadDecision> PreparePreConnectPayloadAsync(
        ITunDevice device,
        TcpRelayState state,
        IPPacket packet,
        int destPort,
        string destIP,
        CancellationToken ct)
    {
        await state.SendLock.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref state.ConnectStarted) != 0)
            {
                if (state.InitialPayloadLength > 0)
                {
                    TryAppendInitialPayloadAndAck(device, state, packet, destIP);
                }

                return PreConnectPayloadDecision.Wait;
            }

            if (!ShouldBufferInitialTlsPayload(state, packet, destPort, destIP))
            {
                if (Interlocked.CompareExchange(ref state.ConnectStarted, 1, 0) != 0)
                {
                    return PreConnectPayloadDecision.Wait;
                }

                return new PreConnectPayloadDecision(true, packet.Payload, false);
            }

            if (!TryAppendInitialPayloadAndAck(device, state, packet, destIP, out var initialPayload))
            {
                return PreConnectPayloadDecision.Wait;
            }

            if (ProtocolInspector.ExtractSni(initialPayload) == null &&
                ProtocolInspector.ShouldWaitForMoreTlsClientHello(initialPayload))
            {
                Log.Debug(
                    "[CONN] buffering partial TLS ClientHello for {DestIP}:443 ({Bytes} bytes)",
                    destIP,
                    initialPayload.Length);
                return PreConnectPayloadDecision.Wait;
            }

            if (Interlocked.CompareExchange(ref state.ConnectStarted, 1, 0) != 0)
            {
                return PreConnectPayloadDecision.Wait;
            }

            return new PreConnectPayloadDecision(true, initialPayload, true);
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    private static bool IsIpv6Packet(ReadOnlySpan<byte> data) =>
        data.Length > 0 && ((data[0] >> 4) & 0x0F) == 6;

    private bool ShouldBufferInitialTlsPayload(TcpRelayState state, IPPacket packet, int destPort, string destIP)
    {
        if (destPort != 443 ||
            packet.Payload.Length == 0 ||
            _dnsStore?.GetCachedHostname(destIP) != null)
        {
            return false;
        }

        return state.InitialPayloadLength > 0 || ProtocolInspector.LooksLikeTlsClientHello(packet.Payload);
    }

    private bool TryAppendInitialPayloadAndAck(
        ITunDevice device,
        TcpRelayState state,
        IPPacket packet,
        string destIP) =>
        TryAppendInitialPayloadAndAck(device, state, packet, destIP, out _);

    private bool TryAppendInitialPayloadAndAck(
        ITunDevice device,
        TcpRelayState state,
        IPPacket packet,
        string destIP,
        out byte[] initialPayload)
    {
        initialPayload = state.GetInitialPayloadSnapshot();
        var tcpFlags = packet.TCPHeader!.Value;
        var incomingEnd = tcpFlags.SequenceNumber + (uint)packet.Payload.Length;

        if (ProtocolInspector.IsSeqBeforeOrEqual(incomingEnd, state.ExpectedClientSeq))
        {
            TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
            return false;
        }

        if (tcpFlags.SequenceNumber != state.ExpectedClientSeq)
        {
            TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
            return false;
        }

        if (state.InitialPayloadLength + packet.Payload.Length > MaxInitialPayloadBufferBytes)
        {
            Log.Warning(
                "[CONN] initial TLS buffer for {DestIP}:443 exceeded {Limit} bytes; continuing with buffered data only",
                destIP,
                MaxInitialPayloadBufferBytes);
            return state.InitialPayloadLength > 0;
        }

        initialPayload = state.AppendInitialPayload(packet.Payload);
        state.ExpectedClientSeq = incomingEnd;
        TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
        return true;
    }

    private void StartInitialConnectAndSend(
        ITunDevice device,
        TcpConnection conn,
        string connKey,
        TcpConnectionManager connManager,
        TcpRelayState state,
        IPPacket packet,
        string connectHost,
        int destPort,
        string routeLabel,
        string srcLabel,
        string destIP,
        string? dnsCacheHost,
        byte[] initialPayload,
        bool initialPayloadAlreadyAcked,
        CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            try
            {
                _dnsStore?.MarkCachedAddressActive(dnsCacheHost, destIP);
                await conn.ConnectAsync(connectHost, destPort, ct);

                if (!_relayStates.ContainsKey(connKey))
                {
                    connManager.RemoveConnectionByKey(connKey);
                    _metrics.DecrementActiveConnections();
                    return;
                }

                state.IsProxyConnected = true;
                state.ConnectionManager = connManager;
                _ = Task.Run(() => RelayProxyToTunAsync(device, conn, connKey, connManager, ct), ct);

                Log.Debug(
                    "[CONN] {Host}:{Port} connected in {ElapsedMs} ms ({Route}/{Source})",
                    connectHost,
                    destPort,
                    (DateTime.UtcNow - started).TotalMilliseconds,
                    routeLabel,
                    srcLabel);

                await SendInitialClientPayloadAsync(
                    device,
                    conn,
                    state,
                    packet,
                    connKey,
                    initialPayload,
                    initialPayloadAlreadyAcked,
                    ct);
            }
            catch (Exception ex)
            {
                HandleConnectionException(ex, connKey, device, packet, connectHost, destPort, routeLabel, srcLabel, destIP, dnsCacheHost);
            }
        }, ct);
    }

    private async Task SendInitialClientPayloadAsync(
        ITunDevice device,
        TcpConnection conn,
        TcpRelayState state,
        IPPacket packet,
        string connKey,
        byte[] initialPayload,
        bool payloadAlreadyAcked,
        CancellationToken ct)
    {
        await state.SendLock.WaitAsync(ct);
        try
        {
            if (!_relayStates.ContainsKey(connKey))
            {
                return;
            }

            var payloadToSend = payloadAlreadyAcked ? state.TakeInitialPayload() : initialPayload;
            if (payloadToSend.Length == 0)
            {
                payloadToSend = initialPayload;
            }

            await conn.SendAsync(payloadToSend, ct);
            _metrics.AddBytesSent(payloadToSend.Length);

            if (!payloadAlreadyAcked)
            {
                var tcpFlags = packet.TCPHeader!.Value;
                state.ExpectedClientSeq = tcpFlags.SequenceNumber + (uint)packet.Payload.Length;
                TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
            }
        }
        catch
        {
            HandleConnectionFail(connKey, device, packet);
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    private async Task SendClientPayloadAsync(
        ITunDevice device,
        TcpConnection conn,
        TcpRelayState state,
        IPPacket packet,
        string connKey,
        CancellationToken ct)
    {
        await state.SendLock.WaitAsync(ct);
        try
        {
            if (!_relayStates.ContainsKey(connKey))
            {
                return;
            }

            var tcpFlags = packet.TCPHeader!.Value;
            var incomingEnd = tcpFlags.SequenceNumber + (uint)packet.Payload.Length;
            if (ProtocolInspector.IsSeqBeforeOrEqual(incomingEnd, state.ExpectedClientSeq))
            {
                TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
                return;
            }

            if (tcpFlags.SequenceNumber != state.ExpectedClientSeq)
            {
                TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
                Log.Debug(
                    "[CONN] out-of-order TCP payload ignored for {ConnKey}: seq={Seq}, expected={Expected}, bytes={Bytes}",
                    connKey,
                    tcpFlags.SequenceNumber,
                    state.ExpectedClientSeq,
                    packet.Payload.Length);
                return;
            }

            state.ExpectedClientSeq = incomingEnd;
            await conn.SendAsync(packet.Payload, ct);
            _metrics.AddBytesSent(packet.Payload.Length);
            TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
        }
        catch
        {
            HandleConnectionFail(connKey, device, packet);
        }
        finally
        {
            state.SendLock.Release();
        }
    }

    private void HandleConnectionException(
        Exception ex,
        string connKey,
        ITunDevice device,
        IPPacket packet,
        string connectHost,
        int destPort,
        string routeLabel,
        string srcLabel,
        string destIP,
        string? dnsCacheHost)
    {
        var reason = ex.Message.Contains("PROXY_DENIED") ? "proxy denied"
                   : ex.Message.Contains("CONNECT_FAILED") ? "connect failed"
                   : "error";
        _lastTcpConnectFailure = $"{connectHost}:{destPort} {routeLabel}/{srcLabel} {reason}: {ex.GetType().Name}: {ex.Message}";
        _lastTcpConnectFailureUtc = DateTime.UtcNow;
        var bindAddress = routeLabel == "PROXY"
            ? _outboundBindAddress?.ToString() ?? "default"
            : "default";
        Log.Warning(ex,
            "[CONN] {Host}:{Port} dropped ({Route}/{Source}; {Reason}; DestIP={DestIP}; Bind={BindAddress})",
            connectHost,
            destPort,
            routeLabel,
            srcLabel,
            reason,
            destIP,
            bindAddress);

        if (ex.Message.Contains("PROXY_DENIED"))
        {
            _ipCache!.TryAddProxyBlocked(destIP);
        }
        else if (routeLabel == "PROXY" && ex.Message.Contains("CONNECT_FAILED"))
        {
            _ipCache!.RecordConnectFailed(destIP);
        }

        _dnsStore?.RemoveCachedAddress(dnsCacheHost, destIP);
        HandleConnectionFail(connKey, device, packet);
    }

    private async Task EnsureDirectBypassRouteAsync(string destIP, IPAddress destinationAddress, RouteDecision decision)
    {
        if (_routeService == null || ShouldSkipDirectBypassRoute(destinationAddress))
        {
            return;
        }

        var routeTask = _directBypassRoutes.GetOrAdd(
            destIP,
            static (ip, state) => new Lazy<Task>(
                () => state.service.AddDirectBypassRouteAsync(ip, state.decision),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (service: this, decision));

        await routeTask.Value;
    }

    private Task AddDirectBypassRouteAsync(string destIP, RouteDecision decision) =>
        Task.Run(() =>
        {
            var ok = _routeService?.AddBypassRoute(destIP) == true;
            if (!ok)
            {
                _directBypassRoutes.TryRemove(destIP, out _);
            }

            Log.Information("[ROUTE] Direct bypass route {Status}: {IP} ({Reason})",
                ok ? "ready" : "failed",
                destIP,
                decision.Reason);
        });

    private bool ShouldSkipDirectBypassRoute(IPAddress destinationAddress)
    {
        if (destinationAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(destinationAddress))
        {
            return true;
        }

        if (IPAddress.TryParse(_config.Tun.IpAddress, out var tunIp) &&
            destinationAddress.Equals(tunIp))
        {
            return true;
        }

        var bytes = destinationAddress.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        var gateway = _routeService?.GetOriginalDefaultGateway();
        return IPAddress.TryParse(gateway, out var gatewayIp) &&
               destinationAddress.Equals(gatewayIp);
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
        if (_config.Route.EnableGeo && _geoIpService != null)
        {
            _ = Task.Run(() => InitializeRuleServiceWithRetryAsync(
                "GEO",
                () => _geoIpService.IsInitialized,
                token => InitializeGeoIpServiceAsync(
                    token,
                    _config.Proxy,
                    waitForProxyReadyWhenNeeded: true,
                    downloadIfMissing: false),
                _cts!.Token));
        }

        if (_config.Route.EnableGfwList && _gfwListService != null)
        {
            _ = Task.Run(() => InitializeRuleServiceWithRetryAsync(
                "GFW",
                () => _gfwListService.IsInitialized,
                token => InitializeGfwListServiceAsync(
                    token,
                    _config.Proxy,
                    waitForProxyReadyWhenNeeded: true,
                    downloadIfMissing: false),
                _cts!.Token));
        }
    }

    private async Task InitializeRuleServiceWithRetryAsync(
        string name,
        Func<bool> isReady,
        Func<CancellationToken, Task<bool>> initialize,
        CancellationToken ct)
    {
        if (isReady())
        {
            return;
        }

        Interlocked.Increment(ref _downloadingCount);
        try
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested && !isReady())
            {
                attempt++;
                try
                {
                    if (await initialize(ct))
                    {
                        Log.Information("[{Name}] Rule resource ready after background attempt {Attempt}.", name, attempt);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[{Name}] Background initialization attempt {Attempt} failed.", name, attempt);
                }

                Log.Warning("[{Name}] Rule resource is not ready; retrying in 60 seconds.", name);
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _downloadingCount);
        }
    }

    private bool NeedsRuleResourceInitialization() =>
        (_config.Route.EnableGeo && _geoIpService?.IsInitialized != true) ||
        (_config.Route.EnableGfwList && _gfwListService?.IsInitialized != true);

    private async Task<bool> InitializeRuleServicesAsync(
        CancellationToken ct,
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded,
        bool downloadIfMissing)
    {
        var ready = true;
        if (_config.Route.EnableGeo && _geoIpService != null)
        {
            Interlocked.Increment(ref _downloadingCount);
            try
            {
                ready &= await InitializeGeoIpServiceAsync(ct, proxyConfig, waitForProxyReadyWhenNeeded, downloadIfMissing);
            }
            finally
            {
                Interlocked.Decrement(ref _downloadingCount);
            }
        }

        if (_config.Route.EnableGfwList && _gfwListService != null)
        {
            Interlocked.Increment(ref _downloadingCount);
            try
            {
                ready &= await InitializeGfwListServiceAsync(ct, proxyConfig, waitForProxyReadyWhenNeeded, downloadIfMissing);
            }
            finally
            {
                Interlocked.Decrement(ref _downloadingCount);
            }
        }

        return ready;
    }

    private async Task<bool> InitializeGeoIpServiceAsync(
        CancellationToken ct,
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded,
        bool downloadIfMissing)
    {
        if (ProxyHttpClientFactory.BuildProxyUri(proxyConfig) == null && waitForProxyReadyWhenNeeded)
        {
            await _proxyReady.Task;
        }

        return await _geoIpService!.InitializeAsync(ct, proxyConfig, downloadIfMissing);
    }

    private async Task<bool> InitializeGfwListServiceAsync(
        CancellationToken ct,
        ProxyConfig? proxyConfig,
        bool waitForProxyReadyWhenNeeded,
        bool downloadIfMissing)
    {
        if (ProxyHttpClientFactory.BuildProxyUri(proxyConfig) == null && waitForProxyReadyWhenNeeded)
        {
            await _proxyReady.Task;
        }

        return await _gfwListService!.InitializeAsync(ct, proxyConfig, downloadIfMissing);
    }

    private void StartMetricsLogger(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(ct))
            {
                Log.Information(
                    "Traffic: sent {Sent} B, received {Received} B, active {Active}, relays {Relays}, dns tcp {DnsTcpOk}/{DnsTcpTotal}, doh {DohOk}/{DohTotal}, last connect failure {LastFailure}",
                    _metrics.TotalBytesSent,
                    _metrics.TotalBytesReceived,
                    (_connectionManager?.ActiveConnections ?? 0) + (_directConnectionManager?.ActiveConnections ?? 0),
                    _relayStates.Count,
                    _dnsProxy?.GetDiagnostics().TcpSuccesses ?? 0,
                    _dnsProxy?.GetDiagnostics().TcpQueries ?? 0,
                    _dnsProxy?.GetDiagnostics().DohSuccesses ?? 0,
                    _dnsProxy?.GetDiagnostics().DohQueries ?? 0,
                    _lastTcpConnectFailure ?? "(none)");
            }
        }, ct);
    }

    private IPAddress? DeterminePreferredBindAddress()
    {
        if (IPAddress.TryParse(_config.Proxy.Host, out var proxyAddress) &&
            IPAddress.IsLoopback(proxyAddress))
        {
            return null;
        }

        var probed = ProbeLocalIpForHost(_config.Proxy.Host, _config.Proxy.Port);
        if (probed != null && !IPAddress.IsLoopback(probed))
        {
            return probed;
        }

        var originalGateway = _routeService?.GetOriginalDefaultGateway();
        if (!string.IsNullOrWhiteSpace(originalGateway))
        {
            var gatewayAddress = FindLocalAddressForGateway(originalGateway);
            if (gatewayAddress != null)
            {
                Log.Information(
                    "[TUN ] Outbound bind address selected from gateway {Gateway}: {BindAddress}",
                    originalGateway,
                    gatewayAddress);
                return gatewayAddress;
            }
        }

        if (probed != null)
        {
            return null;
        }

        Log.Warning("[TUN ] Could not select a stable outbound bind address; proxy connections will use the OS route table.");
        return null;
    }

    private static IPAddress? FindLocalAddressForGateway(string gateway)
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    networkInterface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    networkInterface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var properties = networkInterface.GetIPProperties();
                if (!properties.GatewayAddresses.Any(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        address.Address.ToString().Equals(gateway, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var address = properties.UnicastAddresses
                    .Select(static unicast => unicast.Address)
                    .FirstOrDefault(static address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address));

                if (address != null)
                {
                    return address;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[TUN ] Failed to select outbound bind address for gateway {Gateway}: {Message}", gateway, ex.Message);
        }

        return null;
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

    private void AddProxyBypassRoutes()
    {
        if (_routeService == null)
        {
            return;
        }

        if (IPAddress.TryParse(_config.Proxy.Host, out var proxyIp))
        {
            if (!IPAddress.IsLoopback(proxyIp))
            {
                AddProxyBypassRoute(proxyIp.ToString());
            }

            return;
        }

        try
        {
            foreach (var address in Dns.GetHostAddresses(_config.Proxy.Host)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Distinct())
            {
                AddProxyBypassRoute(address.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to resolve upstream proxy host for bypass route: {Host}, {Message}",
                _config.Proxy.Host,
                ex.Message);
        }
    }

    private void AddProxyBypassRoute(string ipAddress)
    {
        var ok = _routeService?.AddBypassRoute(ipAddress) == true;
        if (ok)
        {
            _proxyBypassRoutes.Add(ipAddress);
        }

        Log.Information("[ROUTE] Proxy bypass route {Status}: {IP}", ok ? "ready" : "failed", ipAddress);
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

internal readonly record struct PreConnectPayloadDecision(
    bool ShouldStartConnect,
    byte[] PayloadForTarget,
    bool PayloadAlreadyAcked)
{
    public static PreConnectPayloadDecision Wait { get; } = new(false, [], false);
}

internal class TcpRelayState
{
    private MemoryStream? _initialPayload;

    public uint NextServerSeq;
    public uint ExpectedClientSeq;
    public readonly IPPacket SynPacket;
    public readonly uint ServerIsn;
    public bool IsProxyConnected;
    public int ConnectStarted;
    public TcpConnectionManager? ConnectionManager;
    public readonly SemaphoreSlim SendLock = new(1, 1);
    public int InitialPayloadLength => (int)(_initialPayload?.Length ?? 0);

    public TcpRelayState(IPPacket synPacket, uint serverIsn, uint clientIsn)
    {
        SynPacket = synPacket;
        ServerIsn = serverIsn;
        NextServerSeq = serverIsn + 1;
        ExpectedClientSeq = clientIsn + 1;
    }

    public byte[] AppendInitialPayload(byte[] payload)
    {
        _initialPayload ??= new MemoryStream();
        _initialPayload.Write(payload, 0, payload.Length);
        return _initialPayload.ToArray();
    }

    public byte[] GetInitialPayloadSnapshot() => _initialPayload?.ToArray() ?? [];

    public byte[] TakeInitialPayload()
    {
        var payload = GetInitialPayloadSnapshot();
        ClearInitialPayload();
        return payload;
    }

    public void ClearInitialPayload()
    {
        _initialPayload?.Dispose();
        _initialPayload = null;
    }
}
