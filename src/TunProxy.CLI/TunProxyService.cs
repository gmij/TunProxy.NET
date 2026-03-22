using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Connections;
using TunProxy.Core.Dns;
using TunProxy.Core.Metrics;
using TunProxy.Core.Packets;
using TunProxy.Core.Wintun;
using ProxyType = TunProxy.Core.Connections.ProxyType;

namespace TunProxy.CLI;

/// <summary>
/// TUN 代理主程序
/// </summary>
public class TunProxyService
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly TunProxy.Core.Connections.ProxyType _proxyType;
    private readonly string? _username;
    private readonly string? _password;
    private WintunAdapter? _adapter;
    private TcpConnectionManager? _connectionManager;
    private GeoIpService? _geoIpService;
    private GfwListService? _gfwListService;
    private WindowsRouteService? _routeService;
    private CancellationTokenSource? _cts;
    private readonly List<string> _geoProxy;
    private readonly List<string> _geoDirect;
    private readonly bool _enableGfwList;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly ProxyMetrics _metrics = new();

    public TunProxyService(
        string proxyHost, 
        int proxyPort, 
        TunProxy.Core.Connections.ProxyType proxyType, 
        string? username = null, 
        string? password = null,
        List<string>? geoProxy = null,
        List<string>? geoDirect = null,
        string geoIpDbPath = "GeoLite2-Country.mmdb",
        bool enableGfwList = false,
        string gfwListUrl = "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt",
        string gfwListPath = "gfwlist.txt")
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _username = username;
        _password = password;
        _geoProxy = geoProxy ?? new List<string>();
        _geoDirect = geoDirect ?? new List<string>();
        _geoIpService = new GeoIpService(geoIpDbPath);
        _enableGfwList = enableGfwList;
        if (enableGfwList)
            _gfwListService = new GfwListService(gfwListUrl, gfwListPath);
        _routeService = new WindowsRouteService();
    }

    /// <summary>
    /// 获取指标快照（用于 Web API）
    /// </summary>
    public MetricsSnapshot GetMetrics() => _metrics.GetSnapshot();

    /// <summary>
    /// 获取服务状态（用于 Web API）
    /// </summary>
    public ServiceStatus GetStatus()
    {
        return new ServiceStatus
        {
            IsRunning = _cts != null && !_cts.IsCancellationRequested,
            ProxyHost = _proxyHost,
            ProxyPort = _proxyPort,
            ProxyType = _proxyType.ToString(),
            ActiveConnections = _connectionManager?.ActiveConnections ?? 0,
            Metrics = _metrics.GetSnapshot()
        };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Log.Information("TunProxy 启动中...");
        Log.Information("代理：{Host}:{Port} ({Type})", _proxyHost, _proxyPort, _proxyType);

        try
        {
            // 1. 确保 wintun.dll 存在
            await EnsureWintunDllAsync(ct);

            // 2. 创建 TUN 适配器
            Log.Information("创建 TUN 适配器...");
            _adapter = WintunAdapter.CreateAdapter("TunProxy", "Wintun");
            Log.Information("TUN 适配器创建成功");

            using var session = _adapter.StartSession(0x400000);
            Log.Information("会话启动成功");

            // 3. 配置 TUN 接口 IP 和路由
            Log.Information("配置 TUN 接口 IP...");
            ConfigureTunInterface();
            Log.Information("TUN 接口配置完成");

            // 配置路由模式
            Log.Information("路由模式：{Mode}", "global");
            if (true) // TODO: AutoAddDefaultRoute
            {
                Log.Information("添加默认路由...");
                AddDefaultRoute();
            }

            // 4. 初始化 GeoIP 服务
            if (_geoProxy.Count > 0 || _geoDirect.Count > 0)
            {
                Log.Information("初始化 GeoIP 服务...");
                await _geoIpService!.InitializeAsync();
                Log.Information("GEO 代理规则：{Proxy}", string.Join(",", _geoProxy));
                Log.Information("GEO 直连规则：{Direct}", string.Join(",", _geoDirect));
            }

            // 5. 初始化 GFWList
            if (_enableGfwList && _gfwListService != null)
            {
                Log.Information("初始化 GFWList...");
                await _gfwListService.InitializeAsync();
            }

            // 5. 创建连接管理器
            _connectionManager = new TcpConnectionManager(_proxyHost, _proxyPort, _proxyType, _username, _password);
            Log.Information("连接管理器初始化完成");

            Log.Information("TunProxy 运行中，按 Ctrl+C 停止");
            Log.Information("代理目标：{Host}:{Port}", _proxyHost, _proxyPort);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // 启动流量统计定时器
            _ = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var snapshot = _metrics.GetSnapshot();
                    Log.Information("流量统计：发送 {Sent:N0} 字节，接收 {Received:N0} 字节，数据包 {Packets} 个，" +
                        "活跃连接 {Active}，总连接 {Total}，失败 {Failed}",
                        snapshot.TotalBytesSent,
                        snapshot.TotalBytesReceived,
                        snapshot.TotalPackets,
                        snapshot.ActiveConnections,
                        snapshot.TotalConnections,
                        snapshot.FailedConnections);
                }
            }, ct);

            try
            {
                await PacketLoop(session, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Information("正在停止...");
            }
        }
        finally
        {
            await StopAsync();
        }
    }

    /// <summary>
    /// 优雅停止服务
    /// </summary>
    public async Task StopAsync()
    {
        Log.Information("开始停止服务...");

        try
        {
            // 1. 取消操作
            _cts?.Cancel();

            // 2. 等待连接关闭
            await Task.Delay(1000);

            // 3. 清理连接
            _connectionManager?.Dispose();
            Log.Information("连接管理器已清理");

            // 4. 删除路由
            try
            {
                _routeService?.RemoveDefaultRoute();
                Log.Information("默认路由已删除");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "删除路由失败");
            }

            // 5. 释放适配器
            _adapter?.Dispose();
            Log.Information("TUN 适配器已释放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止服务时出错");
        }

        Log.Information("服务已停止");
    }

    /// <summary>
    /// 自动下载 wintun.dll
    /// </summary>
    private static async Task EnsureWintunDllAsync(CancellationToken ct)
    {
        var wintunPath = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
        if (File.Exists(wintunPath))
        {
            Log.Debug("wintun.dll 已存在：{Path}", wintunPath);
            return;
        }

        Log.Information("下载 wintun.dll...");
        
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        
        var downloadUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
        var tempZip = Path.Combine(Path.GetTempPath(), "wintun.zip");
        
        try
        {
            var data = await client.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(tempZip, data, ct);
            
            ZipFile.ExtractToDirectory(tempZip, Path.GetTempPath(), true);
            
            var dllFiles = Directory.GetFiles(Path.GetTempPath(), "wintun.dll", SearchOption.AllDirectories);
            if (dllFiles.Length == 0)
                throw new Exception("未找到 wintun.dll");
            
            File.Copy(dllFiles[0], wintunPath, true);
            Log.Information("wintun.dll 下载完成：{Path}", wintunPath);
            
            File.Delete(tempZip);
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "wintun*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "wintun.dll 下载失败");
            Log.Information("请手动下载：https://www.wintun.net/builds/wintun-0.14.1.zip");
            throw;
        }
    }

    private void ConfigureTunInterface()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "interface ip set address \"TunProxy\" static 10.0.0.1 255.255.255.0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            
            Log.Information("TUN 接口配置成功");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动配置 TUN 接口失败");
            Log.Information("请手动运行：netsh interface ip set address \"TunProxy\" static 10.0.0.1 255.255.255.0");
        }
    }

    private void AddDefaultRoute()
    {
        try
        {
            _routeService?.AddDefaultRoute();
            Log.Information("默认路由添加成功");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "添加默认路由失败");
        }
    }

    /// <summary>
    /// 执行路由诊断
    /// </summary>
    public void RunRouteDiagnosis()
    {
        Log.Information("开始路由诊断...");
        var result = _routeService?.Diagnose();
        result?.Print();
    }

    private async Task PacketLoop(WintunSession session, CancellationToken ct)
    {
        var readWaitEvent = session.ReadWaitEvent;
        var cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        while (!ct.IsCancellationRequested)
        {
            // 清理空闲连接
            if (await cleanupTimer.WaitForNextTickAsync(ct))
            {
                _connectionManager?.CleanupIdleConnections(TimeSpan.FromMinutes(10));
                Log.Information("活跃连接数：{Count}", _connectionManager?.ActiveConnections ?? 0);
            }

            var packet = session.ReceivePacket(out var packetSize);

            if (packet != IntPtr.Zero)
            {
                try
                {
                    var data = new byte[packetSize];
                    System.Runtime.InteropServices.Marshal.Copy(packet, data, 0, (int)packetSize);
                    _ = ProcessPacketAsync(session, data, ct);
                }
                finally
                {
                    session.ReleaseReceivePacket(packet);
                }
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
            if (packet == null)
                return;

            _metrics.IncrementPackets();

            // 处理 DNS 请求（UDP 53）
            if (packet.IsUDP && packet.DestinationPort == 53)
            {
                Log.Debug("DNS 请求：{Source} -> {Dest}", packet.Header.SourceAddress, packet.Header.DestinationAddress);
                _metrics.IncrementDnsQueries();
                await ProcessDnsQueryAsync(session, packet, ct);
                return;
            }

            // 只处理 TCP 流量
            if (!packet.IsTCP || packet.SourcePort == null || packet.DestinationPort == null)
                return;

            var destPort = packet.DestinationPort.Value;

            // 只代理 HTTP (80) 和 HTTPS (443)
            if (destPort != 80 && destPort != 443)
                return;

            var destIP = packet.Header.DestinationAddress.ToString();;

            // 路由判断顺序：GFWList > GEO > 默认
            bool shouldProxy = true;

            // 1. GFWList 判断（优先级最高）
            if (_enableGfwList && _gfwListService != null)
            {
                // TODO: 需要 DNS 解析获取域名
                // 暂时简化处理，如果有 GFWList 就默认走代理
                Log.Debug("GFWList 启用：{IP}:{Port}", destIP, destPort);
            }

            // 2. GEO 判断
            if (_geoIpService != null && (_geoProxy.Count > 0 || _geoDirect.Count > 0))
            {
                var geoShouldProxy = _geoIpService.ShouldProxy(
                    packet.Header.DestinationAddress, 
                    _geoProxy, 
                    _geoDirect);
                
                Log.Debug("GEO 判断：{IP} -> {Country}, 代理：{GeoShouldProxy}", 
                    destIP, _geoIpService.GetCountryCode(packet.Header.DestinationAddress), geoShouldProxy);
                
                shouldProxy = geoShouldProxy;
            }

            if (!shouldProxy)
            {
                Log.Debug("直连：{IP}:{Port}", destIP, destPort);
                return; // 直连，不走代理
            }

            Log.Debug("{SourceIP}:{SourcePort} -> {DestIP}:{DestPort} ({Bytes} bytes)",
                packet.Header.SourceAddress, packet.SourcePort, destIP, destPort, packet.Payload.Length);

            // 获取或创建连接
            var connection = _connectionManager!.GetOrCreateConnection(packet);

            if (connection == null)
            {
                Log.Warning("连接池已满，丢弃数据包");
                return;
            }

            // 如果连接未建立，先连接
            if (!connection.IsConnected)
            {
                _metrics.IncrementTotalConnections();
                _metrics.IncrementActiveConnections();

                var connected = await connection.ConnectAsync(destIP, destPort, ct);
                if (!connected)
                {
                    _metrics.DecrementActiveConnections();
                    _metrics.IncrementFailedConnections();
                    Log.Warning("连接建立失败：{IP}:{Port}", destIP, destPort);
                    return;
                }
                Log.Information("连接建立：{IP}:{Port}", destIP, destPort);
            }

            // 发送数据到代理
            if (packet.Payload.Length > 0)
            {
                await connection.SendAsync(packet.Payload, ct);
                _metrics.AddBytesSent(packet.Payload.Length);

                // 接收响应 - 使用 ArrayPool
                var responseBuffer = _bufferPool.Rent(8192);
                try
                {
                    var bytesRead = await connection.ReceiveAsync(responseBuffer, ct);

                    if (bytesRead > 0)
                    {
                        _metrics.AddBytesReceived(bytesRead);
                        Log.Debug("收到响应 {Bytes} bytes", bytesRead);

                        // 回写到 TUN 设备
                        await WriteResponseToTunAsync(session, packet, responseBuffer.AsSpan(0, bytesRead), ct);
                    }
                }
                finally
                {
                    _bufferPool.Return(responseBuffer);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理数据包失败");
        }
    }

    /// <summary>
    /// 将响应数据包写回 TUN 设备
    /// </summary>
    private static unsafe Task WriteResponseToTunAsync(WintunSession session, IPPacket requestPacket, ReadOnlySpan<byte> responseData, CancellationToken ct)
    {
        var responsePacket = BuildResponsePacket(requestPacket, responseData);
        
        if (responsePacket.Length == 0)
            return Task.CompletedTask;

        var sendPacket = session.AllocateSendPacket((uint)responsePacket.Length);
        if (sendPacket == IntPtr.Zero)
        {
            Log.Warning("分配发送缓冲区失败");
            return Task.CompletedTask;
        }

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(responsePacket, 0, sendPacket, responsePacket.Length);
            session.SendPacket(sendPacket);
        }
        finally
        {
            // Wintun 会自动管理
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 构建响应 IP 包
    /// </summary>
    private static byte[] BuildResponsePacket(IPPacket requestPacket, ReadOnlySpan<byte> responseData)
    {
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();

        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        // 构建 TCP 头部（20 字节）
        var tcpHeader = new byte[20];

        // 源端口和目标端口（网络字节序）
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(2, 2), destPort);

        // 序列号和确认号（使用请求包中的值）
        uint seqNum = requestPacket.TCPHeader?.AckNumber ?? 0;
        uint ackNum = requestPacket.TCPHeader.HasValue
            ? requestPacket.TCPHeader.Value.SequenceNumber + (uint)Math.Max(1, requestPacket.Payload.Length)
            : 0;

        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), seqNum);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);

        // 数据偏移（5 * 4 = 20字节）和标志位
        tcpHeader[12] = 0x50;  // 数据偏移 = 5 (20字节)
        tcpHeader[13] = 0x18;  // PSH + ACK 标志

        // 窗口大小
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), 65535);

        // 校验和（稍后计算）
        tcpHeader[16] = 0;
        tcpHeader[17] = 0;

        // 紧急指针
        tcpHeader[18] = 0;
        tcpHeader[19] = 0;

        // 构建 IP 头部（20 字节）
        var ipHeader = new byte[20];

        // 版本 (4) 和头部长度 (5 * 4 = 20字节)
        ipHeader[0] = 0x45;

        // 服务类型
        ipHeader[1] = 0x00;

        // 总长度
        var totalLength = (ushort)(20 + 20 + responseData.Length);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), totalLength);

        // 标识符
        ipHeader[4] = 0x00;
        ipHeader[5] = 0x00;

        // 标志和片偏移
        ipHeader[6] = 0x40;  // Don't Fragment
        ipHeader[7] = 0x00;

        // TTL
        ipHeader[8] = 0x40;  // 64

        // 协议 (TCP = 6)
        ipHeader[9] = 0x06;

        // 校验和（稍后计算）
        ipHeader[10] = 0x00;
        ipHeader[11] = 0x00;

        // 源 IP 和目标 IP
        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);

        // 计算 IP 校验和
        var ipChecksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), ipChecksum);

        // 构建完整的 TCP 段（用于计算校验和）
        var tcpSegment = new byte[20 + responseData.Length];
        Array.Copy(tcpHeader, 0, tcpSegment, 0, 20);
        responseData.CopyTo(tcpSegment.AsSpan(20));

        // 计算 TCP 校验和
        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(
            sourceIP,
            destIP,
            6,  // TCP 协议
            tcpSegment);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);

        // 更新 tcpSegment 中的校验和
        Array.Copy(tcpHeader, 16, tcpSegment, 16, 2);

        // 组装最终数据包
        var packet = new byte[20 + 20 + responseData.Length];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpSegment, 0, packet, 20, tcpSegment.Length);

        return packet;
    }

    /// <summary>
    /// 处理 DNS 查询
    /// </summary>
    private async Task ProcessDnsQueryAsync(WintunSession session, IPPacket requestPacket, CancellationToken ct)
    {
        try
        {
            // 解析 DNS 查询
            var dnsPacket = DnsPacket.Parse(requestPacket.Payload);
            if (dnsPacket == null || dnsPacket.Questions.Count == 0)
            {
                Log.Warning("无效的 DNS 查询包");
                return;
            }

            var domain = dnsPacket.Questions[0].Name;
            Log.Debug("DNS 查询：{Domain}", domain);

            // 通过代理查询 DNS
            var dnsResponse = await QueryDnsViaSocks5Async(domain, requestPacket.Header.DestinationAddress.ToString(), ct);
            if (dnsResponse == null || dnsResponse.Length == 0)
            {
                _metrics.IncrementFailedDnsQueries();
                Log.Warning("DNS 查询失败：{Domain}", domain);
                return;
            }

            Log.Debug("DNS 响应：{Domain}, {Bytes} bytes", domain, dnsResponse.Length);

            // 构建 UDP 响应包
            await WriteUdpResponseToTunAsync(session, requestPacket, dnsResponse, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理 DNS 查询失败");
        }
    }

    /// <summary>
    /// 通过 SOCKS5 查询 DNS
    /// </summary>
    private async Task<byte[]?> QueryDnsViaSocks5Async(string domain, string dnsServer, CancellationToken ct)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(_proxyHost, _proxyPort, ct);
            var stream = client.GetStream();

            // SOCKS5 握手
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            var response = new byte[2];
            await stream.ReadExactlyAsync(response, ct);

            if (response[0] != 0x05 || response[1] != 0x00)
            {
                Log.Warning("SOCKS5 握手失败");
                return null;
            }

            // 连接到 DNS 服务器（UDP 关联）
            // 对于 DNS，我们使用 SOCKS5 的 UDP ASSOCIATE 命令
            // 但由于实现复杂性，这里改用直接连接 DNS 服务器的 TCP 方式
            // 注意：标准 DNS 使用 UDP，但也支持 TCP（RFC 1035）

            var connectRequest = new List<byte> { 0x05, 0x01, 0x00, 0x01 };
            var dnsIp = IPAddress.Parse(dnsServer);
            connectRequest.AddRange(dnsIp.GetAddressBytes());
            connectRequest.Add(0x00);  // 端口 53
            connectRequest.Add(0x35);

            await stream.WriteAsync(connectRequest.ToArray(), ct);

            var connectResponse = new byte[256];
            var bytesRead = await stream.ReadAsync(connectResponse, ct);

            if (bytesRead < 10 || connectResponse[1] != 0x00)
            {
                Log.Warning("SOCKS5 连接 DNS 服务器失败");
                return null;
            }

            // DNS-over-TCP 需要在查询前加上 2 字节的长度字段
            var originalQuery = DnsPacket.Parse(new byte[0])?.Build() ?? Array.Empty<byte>();
            if (originalQuery.Length == 0)
            {
                // 构建标准 DNS A 记录查询
                var dnsQuery = new DnsPacket
                {
                    TransactionId = (ushort)Random.Shared.Next(0, 65536),
                    Flags = new DnsFlags(0x0100), // 标准查询
                    Questions = new List<DnsQuestion>
                    {
                        new DnsQuestion
                        {
                            Name = domain,
                            Type = 1,  // A 记录
                            Class = 1  // IN
                        }
                    }
                };
                originalQuery = dnsQuery.Build();
            }

            var tcpQuery = new byte[originalQuery.Length + 2];
            NetworkHelper.WriteUInt16BigEndian(tcpQuery.AsSpan(0, 2), (ushort)originalQuery.Length);
            Array.Copy(originalQuery, 0, tcpQuery, 2, originalQuery.Length);

            await stream.WriteAsync(tcpQuery, ct);

            // 读取响应长度
            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, ct);
            var responseLength = NetworkHelper.ReadUInt16BigEndian(lengthBytes);

            // 读取 DNS 响应
            var dnsResponseData = new byte[responseLength];
            await stream.ReadExactlyAsync(dnsResponseData, ct);

            return dnsResponseData;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SOCKS5 DNS 查询异常：{Domain}", domain);
            return null;
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// 写入 UDP 响应到 TUN 设备
    /// </summary>
    private static Task WriteUdpResponseToTunAsync(WintunSession session, IPPacket requestPacket, ReadOnlySpan<byte> responseData, CancellationToken ct)
    {
        var responsePacket = BuildUdpResponsePacket(requestPacket, responseData);

        if (responsePacket.Length == 0)
            return Task.CompletedTask;

        var sendPacket = session.AllocateSendPacket((uint)responsePacket.Length);
        if (sendPacket == IntPtr.Zero)
        {
            Log.Warning("分配发送缓冲区失败");
            return Task.CompletedTask;
        }

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(responsePacket, 0, sendPacket, responsePacket.Length);
            session.SendPacket(sendPacket);
        }
        finally
        {
            // Wintun 会自动管理
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 构建 UDP 响应包
    /// </summary>
    private static byte[] BuildUdpResponsePacket(IPPacket requestPacket, ReadOnlySpan<byte> responseData)
    {
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();

        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        // 构建 UDP 头部（8 字节）
        var udpHeader = new byte[8];
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(2, 2), destPort);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(4, 2), (ushort)(8 + responseData.Length));
        // 校验和先设为 0
        udpHeader[6] = 0;
        udpHeader[7] = 0;

        // 构建 IP 头部（20 字节）
        var ipHeader = new byte[20];
        ipHeader[0] = 0x45;  // 版本 4, IHL 5
        ipHeader[1] = 0x00;  // TOS

        var totalLength = (ushort)(20 + 8 + responseData.Length);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), totalLength);

        ipHeader[4] = 0x00;  // 标识
        ipHeader[5] = 0x00;
        ipHeader[6] = 0x40;  // Flags: Don't Fragment
        ipHeader[7] = 0x00;
        ipHeader[8] = 0x40;  // TTL = 64
        ipHeader[9] = 0x11;  // 协议 = UDP (17)

        // 校验和稍后计算
        ipHeader[10] = 0x00;
        ipHeader[11] = 0x00;

        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);

        // 计算 IP 校验和
        var ipChecksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), ipChecksum);

        // 构建完整的 UDP 段（用于计算校验和）
        var udpSegment = new byte[8 + responseData.Length];
        Array.Copy(udpHeader, 0, udpSegment, 0, 8);
        responseData.CopyTo(udpSegment.AsSpan(8));

        // 计算 UDP 校验和
        var udpChecksum = NetworkHelper.CalculateTcpUdpChecksum(
            sourceIP,
            destIP,
            17,  // UDP 协议
            udpSegment);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(6, 2), udpChecksum);

        // 更新 udpSegment 中的校验和
        Array.Copy(udpHeader, 6, udpSegment, 6, 2);

        // 组装最终数据包
        var packet = new byte[20 + 8 + responseData.Length];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(udpSegment, 0, packet, 20, udpSegment.Length);

        return packet;
    }
}
