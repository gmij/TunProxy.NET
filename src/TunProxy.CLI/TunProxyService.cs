using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Connections;
using TunProxy.Core.Metrics;
using TunProxy.Core.Packets;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// TUN 代理主服务（精简的协调器）
/// </summary>
public class TunProxyService
{
    private readonly AppConfig _config;
    private WintunAdapter? _adapter;
    private TcpConnectionManager? _connectionManager;
    private TcpConnectionManager? _directConnectionManager;
    private GeoIpService? _geoIpService;
    private GfwListService? _gfwListService;
    private WindowsRouteService? _routeService;
    private IpCacheManager? _ipCache;
    private DnsProxyService? _dnsProxy;
    private CancellationTokenSource? _cts;
    private readonly ProxyMetrics _metrics = new();
    
    private readonly ConcurrentDictionary<string, TcpRelayState> _relayStates = new();
    private readonly TaskCompletionSource _proxyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TunProxyService(AppConfig config)
    {
        _config = config;
        
        if (config.Route.EnableGeo)
            _geoIpService = new GeoIpService(config.Route.GeoIpDbPath);
        if (config.Route.EnableGfwList)
            _gfwListService = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
        
        _routeService = new WindowsRouteService(config.Tun.IpAddress, config.Tun.SubnetMask);
        _ipCache = new IpCacheManager(_routeService);
        _dnsProxy = new DnsProxyService(config.Proxy.Host, config.Proxy.Port, config.Proxy.GetProxyType(), _ipCache);
    }

    public ServiceStatus GetStatus() => new()
    {
        IsRunning = _cts != null && !_cts.IsCancellationRequested,
        ProxyHost = _config.Proxy.Host,
        ProxyPort = _config.Proxy.Port,
        ProxyType = _config.Proxy.Type,
        ActiveConnections = (_connectionManager?.ActiveConnections ?? 0) + (_directConnectionManager?.ActiveConnections ?? 0),
        Metrics = _metrics.GetSnapshot()
    };

    public async Task StartAsync(CancellationToken ct)
    {
        Log.Information("TunProxy 启动中...");
        Log.Information("代理：{Host}:{Port} ({Type})", _config.Proxy.Host, _config.Proxy.Port, _config.Proxy.Type);

        try
        {
            await EnsureWintunDllAsync(ct);

            _adapter = WintunAdapter.CreateAdapter("TunProxy", "Wintun");
            using var session = _adapter.StartSession(0x400000); // 4MB

            ConfigureTunInterface();

            IPAddress? proxyBindAddr = ProbeLocalIpForHost(_config.Proxy.Host, _config.Proxy.Port);
            
            if (_config.Route.AutoAddDefaultRoute)
            {
                _routeService!.AddBypassRoute(_config.Proxy.Host);
                AddDnsServersBypassRoutes();
                _ipCache!.LoadAndApplyDirectIpCache();
                _ipCache.LoadBlockedIpCache();
                _routeService.AddDefaultRoute();
            }

            _connectionManager = new TcpConnectionManager(_config.Proxy.Host, _config.Proxy.Port, _config.Proxy.GetProxyType(), _config.Proxy.Username, _config.Proxy.Password, bindAddress: proxyBindAddr);
            _directConnectionManager = new TcpConnectionManager(string.Empty, 0, ProxyType.Direct);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            InitializeBackgroundServices();
            StartMetricsLogger(_cts.Token);

            Log.Information("TunProxy 运行中，按 Ctrl+C 停止");
            await Task.Run(() => PacketLoop(session, _cts.Token), _cts.Token);
        }
        finally
        {
            await StopAsync();
        }
    }

    public async Task StopAsync()
    {
        Log.Information("开始停止服务...");
        try
        {
            _cts?.Cancel();
            await Task.Delay(1000); // 留时间给清理

            _connectionManager?.Dispose();
            _directConnectionManager?.Dispose();

            _routeService?.RemoveBypassRoute(_config.Proxy.Host);
            _ipCache?.CleanupBypassRoutes();
            _routeService?.RemoveDefaultRoute();

            _adapter?.Dispose();
        }
        catch (Exception ex) { Log.Error(ex, "停止服务出错"); }
    }

    private void PacketLoop(WintunSession session, CancellationToken ct)
    {
        _proxyReady.TrySetResult();
        var readWaitEvent = session.ReadWaitEvent;

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
            var packet = session.ReceivePacket(out var packetSize);
            if (packet != IntPtr.Zero)
            {
                try
                {
                    var data = new byte[packetSize];
                    System.Runtime.InteropServices.Marshal.Copy(packet, data, 0, (int)packetSize);
                    _metrics.IncrementRawPacketsReceived();
                    _ = ProcessPacketAsync(session, data, ct);
                }
                finally { session.ReleaseReceivePacket(packet); }
            }
            else if (WintunNative.ERROR_NO_MORE_ITEMS == System.Runtime.InteropServices.Marshal.GetLastWin32Error())
            {
                WintunNative.WaitForSingleObject(readWaitEvent, 100);
            }
        }
    }

    private async Task ProcessPacketAsync(WintunSession session, byte[] data, CancellationToken ct)
    {
        try
        {
            var packet = IPPacket.Parse(data);
            if (packet == null) { _metrics.IncrementParseFailures(); return; }

            _metrics.IncrementPackets();

            // DNS 处理
            if (packet.IsUDP && packet.DestinationPort == 53)
            {
                _metrics.IncrementDnsQueries();
                await _dnsProxy!.ProcessDnsQueryAsync(session, packet, ct);
                return;
            }

            // TCP 包及端口过滤
            if (!packet.IsTCP || packet.SourcePort == null || packet.DestinationPort == null)
            {
                _metrics.IncrementNonTcpUdpPackets();
                return;
            }

            var tcpFlags = packet.TCPHeader!.Value;
            var destPort = packet.DestinationPort.Value;
            var destIP = packet.Header.DestinationAddress.ToString();

            if (packet.Payload.Length == 0 && !tcpFlags.SYN && !tcpFlags.FIN && !tcpFlags.RST) return;
            if (ProtocolInspector.IsPrivateIp(packet.Header.DestinationAddress) || (destPort != 80 && destPort != 443))
            {
                if (destPort != 80 && destPort != 443) _metrics.IncrementPortFilteredPackets();
                if (tcpFlags.SYN) TunWriter.WriteRst(session, packet);
                return;
            }

            // 路由决策
            bool shouldProxy = true;
            if (_config.Route.EnableGeo && _geoIpService != null)
                shouldProxy = _geoIpService.ShouldProxy(packet.Header.DestinationAddress, _config.Route.GeoProxy, _config.Route.GeoDirect);

            if (!shouldProxy) _metrics.IncrementDirectRoutedPackets();

            var connManager = shouldProxy ? _connectionManager! : _directConnectionManager!;
            var connKey = ProtocolInspector.MakeConnectionKey(packet);

            // TCP 握手 (SYN)
            if (tcpFlags.SYN && !tcpFlags.ACK)
            {
                if (!shouldProxy && _routeService != null)
                {
                    _ipCache!.TryAddDirectBypass(ProtocolInspector.GetNet24(destIP));
                    TunWriter.WriteRst(session, packet);
                    return;
                }
                if (_ipCache!.IsProxyBlocked(destIP) || _ipCache.IsConnectFailed(destIP))
                {
                    TunWriter.WriteRst(session, packet);
                    return;
                }

                TunWriter.WriteSynAck(session, packet, out var serverIsn);
                _relayStates[connKey] = new TcpRelayState(packet, serverIsn, tcpFlags.SequenceNumber);
                return;
            }

            if (!_relayStates.TryGetValue(connKey, out var state))
            {
                if (packet.Payload.Length > 0) TunWriter.WriteRst(session, packet);
                else connManager.RemoveConnectionByKey(connKey);
                return;
            }

            // TCP 断开连接 (FIN/RST)
            if (tcpFlags.FIN || tcpFlags.RST)
            {
                if (_relayStates.TryRemove(connKey, out _))
                {
                    (state.ConnectionManager ?? connManager).RemoveConnectionByKey(connKey);
                    if (state.IsProxyConnected) _metrics.DecrementActiveConnections();
                }
                return;
            }

            // TCP 数据转发
            if (packet.Payload.Length > 0)
            {
                TcpConnection? conn;
                if (!state.IsProxyConnected)
                {
                    if (Interlocked.CompareExchange(ref state.ConnectStarted, 1, 0) != 0) return;
                    _metrics.IncrementTotalConnections();
                    _metrics.IncrementActiveConnections();

                    string connectHost = destPort == 443 
                        ? (ProtocolInspector.ExtractSni(packet.Payload) ?? _ipCache!.GetCachedHostname(destIP) ?? destIP)
                        : (ProtocolInspector.ExtractHttpHost(packet.Payload) ?? _ipCache!.GetCachedHostname(destIP) ?? destIP);

                    if (_config.Route.EnableGfwList && _gfwListService != null && connectHost != destIP)
                    {
                        bool gfwProxy = _gfwListService.IsInGfwList(connectHost);
                        if (gfwProxy != shouldProxy) connManager = gfwProxy ? _connectionManager! : _directConnectionManager!;
                    }

                    conn = connManager.GetOrCreateConnection(packet);
                    if (conn == null) { HandleConnectionFail(connKey, session, packet); return; }

                    try
                    {
                        await conn.ConnectAsync(connectHost, destPort, ct);
                        state.IsProxyConnected = true;
                        state.ConnectionManager = connManager;
                        _ = Task.Run(() => RelayProxyToTunAsync(session, conn, connKey, connManager, ct), ct); // Start receiving
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("PROXY_DENIED")) _ipCache!.TryAddProxyBlocked(destIP);
                        else if (ex.Message.Contains("CONNECT_FAILED")) _ipCache!.RecordConnectFailed(destIP);
                        HandleConnectionFail(connKey, session, packet);
                        return;
                    }
                }
                else
                {
                    conn = (state.ConnectionManager ?? connManager).GetExistingConnection(packet);
                    if (conn == null) return;
                }

                // 检查丢包/重传
                uint incomingEnd = tcpFlags.SequenceNumber + (uint)packet.Payload.Length;
                if (ProtocolInspector.IsSeqBeforeOrEqual(incomingEnd, state.ExpectedClientSeq))
                {
                    TunWriter.WriteAck(session, packet, state.NextServerSeq, state.ExpectedClientSeq);
                    return;
                }

                state.ExpectedClientSeq = incomingEnd;
                try
                {
                    await conn.SendAsync(packet.Payload, ct);
                    _metrics.AddBytesSent(packet.Payload.Length);
                    TunWriter.WriteAck(session, packet, state.NextServerSeq, state.ExpectedClientSeq);
                }
                catch { HandleConnectionFail(connKey, session, packet); }
            }
        }
        catch (Exception ex) { Log.Error(ex, "包处理异常"); }
    }

    private void HandleConnectionFail(string connKey, WintunSession session, IPPacket packet)
    {
        _metrics.DecrementActiveConnections();
        _metrics.IncrementFailedConnections();
        if (_relayStates.TryRemove(connKey, out var state))
            (state.ConnectionManager ?? _connectionManager)?.RemoveConnectionByKey(connKey);
        TunWriter.WriteRst(session, packet);
    }

    private async Task RelayProxyToTunAsync(WintunSession session, TcpConnection connection, string connKey, TcpConnectionManager connManager, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        bool normalClose = false;
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
                catch { break; }

                if (bytesRead == 0) { normalClose = true; break; }
                if (!_relayStates.TryGetValue(connKey, out var state)) break;

                _metrics.AddBytesReceived(bytesRead);
                TunWriter.WriteDataResponse(session, state.SynPacket, buffer.AsSpan(0, bytesRead), state.NextServerSeq, state.ExpectedClientSeq);
                state.NextServerSeq += (uint)bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (normalClose && _relayStates.TryGetValue(connKey, out var s))
                TunWriter.WriteFinAck(session, s.SynPacket, s.NextServerSeq, s.ExpectedClientSeq);
            
            if (_relayStates.TryRemove(connKey, out _))
            {
                connManager.RemoveConnectionByKey(connKey);
                _metrics.DecrementActiveConnections();
            }
        }
    }

    // ---------------- 以下为基础设置 ---------------- //

    private void InitializeBackgroundServices()
    {
        string? httpUrl = _config.Proxy.GetProxyType() == ProxyType.Http ? $"http://{_config.Proxy.Host}:{_config.Proxy.Port}" : null;
        if (_config.Route.EnableGeo && _geoIpService != null)
            _ = Task.Run(async () => { if (httpUrl == null) await _proxyReady.Task; await _geoIpService.InitializeAsync(_cts!.Token, httpUrl); });
        if (_config.Route.EnableGfwList && _gfwListService != null)
            _ = Task.Run(async () => { if (httpUrl == null) await _proxyReady.Task; await _gfwListService.InitializeAsync(_cts!.Token, httpUrl); });
    }

    private void StartMetricsLogger(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(ct))
                Log.Information("流量：发 {Sent} B，收 {Received} B，活跃 {Active}",
                    _metrics.TotalBytesSent, _metrics.TotalBytesReceived, (_connectionManager?.ActiveConnections ?? 0) + (_directConnectionManager?.ActiveConnections ?? 0));
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
        catch { return null; }
    }

    private void AddDnsServersBypassRoutes()
    {
        if (_routeService == null) return;
        var dnsServers = new HashSet<string> { "8.8.8.8", "8.8.4.4", "1.1.1.1", "114.114.114.114", "223.5.5.5" };
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                foreach (var dns in ni.GetIPProperties().DnsAddresses)
                    if (dns.AddressFamily == AddressFamily.InterNetwork && !dns.ToString().StartsWith("127."))
                        dnsServers.Add(dns.ToString());
        
        foreach (var dns in dnsServers) _routeService.AddBypassRoute(dns);
    }

    private void ConfigureTunInterface()
    {
        var runCommand = (string file, string args) =>
        {
            var p = Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(3000);
        };
        runCommand("netsh", $"interface ip set address \"TunProxy\" static {_config.Tun.IpAddress} {_config.Tun.SubnetMask}");
    }

    private static async Task EnsureWintunDllAsync(CancellationToken ct)
    {
        var wintunPath = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
        if (File.Exists(wintunPath)) return;
        
        var url = Environment.Is64BitProcess ? 
            "https://git.zx2c4.com/wintun/plain/bin/amd64/wintun.dll" : 
            "https://git.zx2c4.com/wintun/plain/bin/x86/wintun.dll";
        
        try
        {
            using var client = new HttpClient();
            var data = await client.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(wintunPath, data, ct);
        }
        catch (Exception ex) { Log.Warning("自动下载 Wintun.dll 失败: {Message}", ex.Message); }
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
