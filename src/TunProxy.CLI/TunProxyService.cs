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
    private readonly GeoIpService? _geoIpService;
    private readonly GfwListService? _gfwListService;
    private readonly RuleResourceInitializer _ruleResources;
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
    private TunPacketPipeline? _packetPipeline;
    private IPAddress? _outboundBindAddress;
    private readonly ConcurrentBag<string> _proxyBypassRoutes = new();
    private readonly DirectBypassRouteManager _directBypassRoutes;
    private readonly DirectBypassRouteScheduler _directBypassRouteScheduler;
    private readonly ProxyBypassRouteConfigurator _proxyBypassRouteConfigurator = new();
    private readonly PendingRelayStateCleaner _pendingRelayStateCleaner = new();
    private readonly TunRuntimeStateStore _runtimeStateStore = new();
    private readonly FakeIpPool? _fakeIpPool;
    private readonly UdpDirectRelay _udpDirectRelay = new();
    private string? _lastTcpConnectFailure;
    private DateTime? _lastTcpConnectFailureUtc;
    private DateTime? _lastPacketReadUtc;
    private DateTime? _lastPacketProcessedUtc;

    private const int PacketQueueCapacity = 4096;
    private const int MaxInitialPayloadBufferBytes = 64 * 1024;
    private static readonly TimeSpan PendingRelayStateIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OutboundReadyWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OutboundReadyPollInterval = TimeSpan.FromSeconds(1);

    public TunProxyService(AppConfig config)
    {
        _config = config;

        _geoIpService = new GeoIpService(config.Route.GeoIpDbPath);
        _gfwListService = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
        _ruleResources = new RuleResourceInitializer(
            config,
            _geoIpService,
            _gfwListService,
            () => Interlocked.Increment(ref _downloadingCount),
            () => Interlocked.Decrement(ref _downloadingCount),
            () => _proxyReady.Task);

        _routeDecision = new RouteDecisionService(config, _geoIpService, _gfwListService);
        _routeService = RouteServiceFactory.Create(config.Tun);
        _directBypassRoutes = new DirectBypassRouteManager(_routeService);
        _directBypassRouteScheduler = new DirectBypassRouteScheduler(
            _routeService,
            _directBypassRoutes,
            config.Tun.IpAddress,
            () => _proxyBypassRoutes);
        _ipCache = new IpCacheManager(_routeService);
        _dnsStore = new DnsResolutionStore();

        if (config.Tun.FakeIpMode)
        {
            _fakeIpPool = new FakeIpPool();
        }

        _dnsProxy = new DnsProxyService(
            config.Proxy.Host,
            config.Proxy.Port,
            config.Proxy.GetProxyType(),
            _dnsStore,
            config.Tun.DnsServer,
            config.Proxy.Username,
            config.Proxy.Password,
            _routeDecision,
            // In FakeIP mode bypass-route candidates are not added because traffic always
            // arrives at the fake IP (never at the real IP directly).
            onDirectRouteCandidate: _fakeIpPool != null ? null : _directBypassRouteScheduler.ScheduleAsync,
            fakeIpPool: _fakeIpPool);
    }

    public ServiceStatus GetStatus() => TunRuntimeDiagnosticsProvider.CreateStatus(
        _config,
        _cts,
        _downloadingCount,
        _connectionManager,
        _directConnectionManager,
        _metrics);

    public async Task<IReadOnlyList<DnsRouteRecord>> GetDnsRouteRecordsAsync(CancellationToken ct)
    {
        var snapshots = _dnsStore?.GetResolutionSnapshot() ?? [];
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
                snapshot.SeenCount,
                ProtocolInspector.IsPrivateIp(ipAddress),
                snapshot.IsDnsCached,
                snapshot.DnsLastActiveUtc ?? snapshot.LastSeenUtc));
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
        if (!_ruleResources.NeedsInitialization())
        {
            return;
        }

        await _ruleResources.InitializeEnabledAsync(
            ct,
            _config.Proxy,
            waitForProxyReadyWhenNeeded: false,
            downloadIfMissing: false,
            includeAlreadyInitialized: true);
    }

    public TunDiagnosticsSnapshot GetDiagnostics()
    {
        return TunRuntimeDiagnosticsProvider.CreateDiagnostics(
            _config,
            _cts,
            _outboundBindAddress,
            _packetPipeline,
            _relayStates,
            _metrics,
            _routeService,
            _proxyBypassRoutes,
            _directBypassRoutes,
            _dnsProxy,
            _lastTcpConnectFailure,
            _lastTcpConnectFailureUtc,
            _lastPacketReadUtc,
            _lastPacketProcessedUtc);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Log.Information("Starting TunProxy...");
        Log.Information("Proxy: {Host}:{Port} ({Type})", _config.Proxy.Host, _config.Proxy.Port, _config.Proxy.Type);

        try
        {
            if (_ruleResources.NeedsInitialization())
            {
                Log.Information("[TUN ] Loading existing GFWList/GeoIP resources before applying TUN routes.");
                if (!await _ruleResources.InitializeEnabledAsync(
                        ct,
                        _config.Proxy,
                        waitForProxyReadyWhenNeeded: false,
                        downloadIfMissing: false,
                        includeAlreadyInitialized: true))
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

            if (_config.Route.AutoAddDefaultRoute)
            {
                await WaitForOutboundReadyAsync(ct);
                Log.Information("[ROUTE] Applying proxy bypass routes and TUN default route...");
                AddProxyBypassRoutes();
                SelectOutboundBindAddress();
                _ipCache!.RetireDirectIpCache();
                _ipCache.LoadBlockedIpCache();
                var defaultRouteReady = _routeService!.AddDefaultRoute();
                Log.Information("[ROUTE] Route setup completed. DefaultRouteReady={DefaultRouteReady}", defaultRouteReady);
                if (!defaultRouteReady)
                {
                    Log.Warning("[ROUTE] TUN default route is still missing; system traffic may not enter TUN.");
                }
            }
            else
            {
                SelectOutboundBindAddress();
                if (_fakeIpPool != null)
                {
                    Log.Warning(
                        "[TUN ] FakeIP mode is active but AutoAddDefaultRoute is disabled. " +
                        "Add a manual route for 198.18.0.0/16 pointing to the TUN interface " +
                        "so that fake-IP destinations are captured by TunProxy.");
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
            await Task.Run(() => _packetPipeline!.ReadPacketsAsync(_tunDevice, _proxyReady, _cts.Token), _cts.Token);
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
            _packetPipeline?.Complete();
            if (_packetPipeline != null)
            {
                await Task.WhenAny(_packetPipeline.Completion, Task.Delay(2000));
            }
            await Task.Delay(1000);

            _connectionManager?.Dispose();
            _directConnectionManager?.Dispose();
            _udpDirectRelay.Dispose();

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
        _packetPipeline = TunPacketPipeline.Start(
            PacketQueueCapacity,
            ProcessPacketAsync,
            _metrics,
            timestamp => _lastPacketReadUtc = timestamp,
            ct);
    }

    private void StartRuntimeCleanup(CancellationToken ct)
    {
        _ = PeriodicBackgroundTask.Start(TimeSpan.FromSeconds(30), _ =>
        {
            _connectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(2));
            _directConnectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(2));
            _pendingRelayStateCleaner.Cleanup(_relayStates, PendingRelayStateIdleTimeout);
            _directBypassRoutes.CleanupExpired();
            _dnsStore?.CleanupExpired();
            _udpDirectRelay.CleanupExpired(UdpDirectRelay.DefaultIdleTimeout);
            _fakeIpPool?.CleanupExpired(TimeSpan.FromMinutes(30));
            return Task.CompletedTask;
        }, ct);
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
                if (TunPacketDecisions.IsIpv6Packet(data))
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
                var udpDestIp = packet.Header.DestinationAddress;

                // Fake IPs are virtual 198.18.0.0/16 addresses managed by the FakeIP pool.
                // UDP to them cannot be forwarded to a real peer; reject so apps fall back to TCP.
                if (_fakeIpPool != null && FakeIpPool.IsFakeIp(udpDestIp))
                {
                    TunWriter.WriteIcmpPortUnreachable(device, packet);
                    return;
                }

                var cachedHost = GetCachedHostnameForIp(udpDestIp);
                var udpDecision = await _routeDecision.DecideForTunAsync(cachedHost, udpDestIp, ct);

                if (!TunPacketDecisions.ShouldRejectUdpPacket(udpDecision))
                {
                    // Direct-routed UDP: relay through the physical interface instead of
                    // writing a kernel route entry (which would drop the first datagram).
                    _metrics.IncrementDirectRoutedPackets();
                    Log.Debug("[UDP ] {DestIP}:{Port}  DIRECT  ({Reason}); relaying via application layer",
                        udpDestIp,
                        packet.DestinationPort,
                        udpDecision.Reason);
                    await _udpDirectRelay.ForwardAsync(device, packet, _outboundBindAddress, ct);
                    return;
                }

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
                    existingState.MarkActivity();
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

            state.MarkActivity();

            if (tcpFlags.FIN || tcpFlags.RST)
            {
                if (_relayStates.TryRemove(connKey, out _))
                {
                    (state.ConnectionManager ?? connManager).RemoveConnectionByKey(connKey);
                    state.Dispose();
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

                    var target = TunConnectionDecisions.SelectTarget(
                        destPort,
                        destIP,
                        initialPayload,
                        GetCachedHostnameForIp(packet.Header.DestinationAddress));

                    // For fake-IP destinations the destination address is a virtual placeholder
                    // from the 198.18.0.0/16 pool.  Pass null so that geo / private-IP checks
                    // are based on the domain name (or the real IP resolved from it) rather
                    // than the fake address, which has no geographic meaning.
                    var effectiveDestIp = (_fakeIpPool != null && FakeIpPool.IsFakeIp(packet.Header.DestinationAddress))
                        ? null
                        : (IPAddress?)packet.Header.DestinationAddress;

                    var finalDecision = await _routeDecision.DecideForTunAsync(
                        target.DomainHint,
                        effectiveDestIp,
                        ct);
                    if (target.HasDomainHint)
                    {
                        _dnsStore!.RecordObservedHostname(
                            destIP,
                            target.ConnectHost,
                            target.DomainSource,
                            finalDecision.ShouldProxy ? "PROXY" : "DIRECT",
                            finalDecision.Reason);

                        if (finalDecision.EvaluatedIp != null &&
                            !finalDecision.EvaluatedIp.Equals(packet.Header.DestinationAddress))
                        {
                            _dnsStore!.RecordObservedHostname(
                                finalDecision.EvaluatedIp.ToString(),
                                target.ConnectHost,
                                target.DomainSource,
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
                        // In FakeIP mode the destination is always a virtual address from the
                        // 198.18.0.0/16 pool. Adding a bypass route for it would be incorrect
                        // (the real peer IP is different). Skip route management for fake IPs.
                        if (_fakeIpPool == null || !FakeIpPool.IsFakeIp(packet.Header.DestinationAddress))
                        {
                            await _directBypassRouteScheduler.EnsureAsync(routeIp, finalDecision, ct);
                            state.DirectBypassIp = routeIp.ToString();
                        }
                    }

                    var upstreamHost = TunConnectionDecisions.SelectUpstreamHost(
                        shouldProxy,
                        finalDecision,
                        target);
                    var dnsCacheHost = target.DnsCacheHost;
                    var usingProxy = connManager == _connectionManager;
                    var routeLabel = usingProxy ? "PROXY" : "DIRECT";
                    var srcLabel = $"{target.DomainSource}/{finalDecision.Reason}";
                    Log.Information("[CONN] {Host}:{Port}  {Route}  ({Source})", target.ConnectHost, destPort, routeLabel, srcLabel);

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

    private bool ShouldBufferInitialTlsPayload(TcpRelayState state, IPPacket packet, int destPort, string destIP)
        => TunPacketDecisions.ShouldBufferInitialTlsPayload(
            destPort,
            packet.Payload.Length,
            state.InitialPayloadLength,
            GetCachedHostnameForIp(packet.Header.DestinationAddress) != null,
            packet.Payload);

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
        var sequence = TunTcpPayloadDecisions.EvaluateIncomingPayload(
            tcpFlags.SequenceNumber,
            packet.Payload.Length,
            state.ExpectedClientSeq);

        if (sequence.Action != TcpPayloadSequenceAction.Accept)
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
            return TunTcpPayloadDecisions.CanContinueWithBufferedPayload(state.InitialPayloadLength);
        }

        initialPayload = state.AppendInitialPayload(packet.Payload);
        state.ExpectedClientSeq = sequence.IncomingEnd;
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

            var payloadToSend = TunTcpPayloadDecisions.SelectInitialPayload(
                payloadAlreadyAcked,
                payloadAlreadyAcked ? state.TakeInitialPayload() : [],
                initialPayload);

            _directBypassRoutes.Touch(state.DirectBypassIp);
            await conn.SendAsync(payloadToSend, ct);
            _metrics.AddBytesSent(payloadToSend.Length);

            if (!payloadAlreadyAcked)
            {
                var tcpFlags = packet.TCPHeader!.Value;
                state.ExpectedClientSeq = TunTcpPayloadDecisions.CalculateIncomingEnd(
                    tcpFlags.SequenceNumber,
                    packet.Payload.Length);
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
            var sequence = TunTcpPayloadDecisions.EvaluateIncomingPayload(
                tcpFlags.SequenceNumber,
                packet.Payload.Length,
                state.ExpectedClientSeq);
            if (sequence.Action == TcpPayloadSequenceAction.AlreadyAcknowledged)
            {
                TunWriter.WriteAck(device, packet, state.NextServerSeq, state.ExpectedClientSeq);
                return;
            }

            if (sequence.Action == TcpPayloadSequenceAction.OutOfOrder)
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

            state.ExpectedClientSeq = sequence.IncomingEnd;
            _directBypassRoutes.Touch(state.DirectBypassIp);
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
        var failure = TunConnectionDecisions.ClassifyFailure(ex, routeLabel);
        _lastTcpConnectFailure = $"{connectHost}:{destPort} {routeLabel}/{srcLabel} {failure.Reason}: {ex.GetType().Name}: {ex.Message}";
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
            failure.Reason,
            destIP,
            bindAddress);

        if (failure.ShouldRecordProxyBlocked)
        {
            _ipCache!.TryAddProxyBlocked(destIP);
        }
        else if (failure.ShouldRecordProxyConnectFailed)
        {
            _ipCache!.RecordConnectFailed(destIP);
        }

        _dnsStore?.RemoveCachedAddress(dnsCacheHost, destIP);
        HandleConnectionFail(connKey, device, packet);
    }

    private void HandleConnectionFail(string connKey, ITunDevice device, IPPacket packet)
    {
        _metrics.DecrementActiveConnections();
        _metrics.IncrementFailedConnections();
        if (_relayStates.TryRemove(connKey, out var state))
        {
            (state.ConnectionManager ?? _connectionManager)?.RemoveConnectionByKey(connKey);
            state.Dispose();
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

                state.MarkActivity();
                _directBypassRoutes.Touch(state.DirectBypassIp);
                _metrics.AddBytesReceived(bytesRead);

                foreach (var segment in TunServerResponseSegments.Create(bytesRead))
                {
                    TunWriter.WriteDataResponse(
                        device,
                        state.SynPacket,
                        buffer.AsSpan(segment.Offset, segment.Length),
                        state.NextServerSeq,
                        state.ExpectedClientSeq);
                    state.NextServerSeq += (uint)segment.Length;
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

            if (_relayStates.TryRemove(connKey, out var removed))
            {
                connManager.RemoveConnectionByKey(connKey);
                removed.Dispose();
                _metrics.DecrementActiveConnections();
            }
        }
    }

    private void InitializeBackgroundServices()
    {
        _ruleResources.StartBackgroundRetry(_cts!.Token, _config.Proxy);
    }

    private void StartMetricsLogger(CancellationToken ct)
    {
        _ = PeriodicBackgroundTask.Start(TimeSpan.FromSeconds(60), _ =>
        {
            var snapshot = TunTrafficLogSnapshot.Create(
                _metrics,
                _connectionManager?.ActiveConnections ?? 0,
                _directConnectionManager?.ActiveConnections ?? 0,
                _relayStates.Count,
                _dnsProxy?.GetDiagnostics(),
                _lastTcpConnectFailure);

            Log.Information(
                "Traffic: sent {Sent} B, received {Received} B, active {Active}, relays {Relays}, dns tcp {DnsTcpOk}/{DnsTcpTotal}, doh {DohOk}/{DohTotal}, last connect failure {LastFailure}",
                snapshot.TotalBytesSent,
                snapshot.TotalBytesReceived,
                snapshot.ActiveConnections,
                snapshot.RelayStateCount,
                snapshot.DnsTcpSuccesses,
                snapshot.DnsTcpQueries,
                snapshot.DnsDohSuccesses,
                snapshot.DnsDohQueries,
                snapshot.LastTcpConnectFailure);
            return Task.CompletedTask;
        }, ct);
    }

    private void AddProxyBypassRoutes()
    {
        if (_routeService == null)
        {
            return;
        }

        foreach (var ipAddress in _proxyBypassRouteConfigurator.AddProxyBypassRoutes(_config.Proxy, _routeService))
        {
            _proxyBypassRoutes.Add(ipAddress);
        }
    }

    private async Task WaitForOutboundReadyAsync(CancellationToken ct)
    {
        var selector = new TunOutboundBindAddressSelector();
        var deadline = DateTime.UtcNow + OutboundReadyWaitTimeout;
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            var preferredBindAddress = _runtimeStateStore.LoadLastOutboundBindAddress();
            var selection = selector.SelectWithSource(
                _config.Proxy,
                _routeService,
                preferredBindAddress);

            if (selection.IsReady)
            {
                Log.Information(
                    "[ROUTE] Outbound path ready after {Attempt} attempt(s): Source={Source}, BindAddress={BindAddress}",
                    attempt,
                    selection.Source,
                    selection.Address);
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warning(
                    "[ROUTE] Outbound path was not ready after waiting {TimeoutSeconds}s. LastSource={Source}, LastBindAddress={BindAddress}. Continuing with best-effort startup.",
                    OutboundReadyWaitTimeout.TotalSeconds,
                    selection.Source,
                    selection.Address?.ToString() ?? "(not bound)");
                return;
            }

            if (attempt == 1 || attempt % 5 == 0)
            {
                Log.Information(
                    "[ROUTE] Waiting for outbound path to become ready before applying TUN routes. Attempt={Attempt}, LastSource={Source}, LastBindAddress={BindAddress}",
                    attempt,
                    selection.Source,
                    selection.Address?.ToString() ?? "(not bound)");
            }

            await Task.Delay(OutboundReadyPollInterval, ct);
        }
    }

    /// <summary>
    /// Returns the hostname associated with <paramref name="ip"/>, checking both the DNS
    /// resolution store and (in FakeIP mode) the fake-IP pool.  The fake-IP pool is the
    /// authoritative source for 198.18.0.0/16 addresses.
    /// </summary>
    private string? GetCachedHostnameForIp(IPAddress ip)
    {
        // In FakeIP mode the fake-IP pool holds the canonical domain for pool addresses.
        if (_fakeIpPool != null && FakeIpPool.IsFakeIp(ip))
        {
            return _fakeIpPool.GetDomain(ip);
        }

        return _dnsStore?.GetCachedHostname(ip.ToString());
    }

    private void SelectOutboundBindAddress()
    {
        var preferredBindAddress = _runtimeStateStore.LoadLastOutboundBindAddress();
        _outboundBindAddress = new TunOutboundBindAddressSelector().Select(
            _config.Proxy,
            _routeService,
            preferredBindAddress);
        _runtimeStateStore.SaveLastOutboundBindAddress(_outboundBindAddress);
        Log.Information(
            "[TUN ] Outbound bind address: {BindAddress}",
            _outboundBindAddress?.ToString() ?? "(not bound)");
    }

    private static async Task EnsureWintunDllAsync(CancellationToken ct)
    {
        var wintunPath = AppPaths.WintunDllPath;
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

internal class TcpRelayState : IDisposable
{
    private MemoryStream? _initialPayload;
    private long _lastActivityTicks;

    public uint NextServerSeq;
    public uint ExpectedClientSeq;
    public readonly IPPacket SynPacket;
    public readonly uint ServerIsn;
    public bool IsProxyConnected;
    public int ConnectStarted;
    public TcpConnectionManager? ConnectionManager;
    public readonly SemaphoreSlim SendLock = new(1, 1);
    public string? DirectBypassIp;
    public int InitialPayloadLength => (int)(_initialPayload?.Length ?? 0);
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    public TcpRelayState(IPPacket synPacket, uint serverIsn, uint clientIsn)
    {
        SynPacket = synPacket;
        ServerIsn = serverIsn;
        NextServerSeq = serverIsn + 1;
        ExpectedClientSeq = clientIsn + 1;
        _lastActivityTicks = DateTime.UtcNow.Ticks;
    }

    public void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

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

    public void Dispose()
    {
        ClearInitialPayload();
    }
}
