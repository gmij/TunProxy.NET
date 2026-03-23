using System.Buffers;
using System.Collections.Concurrent;
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
    private TcpConnectionManager? _directConnectionManager;
    private GeoIpService? _geoIpService;
    private GfwListService? _gfwListService;
    private WindowsRouteService? _routeService;
    private CancellationTokenSource? _cts;
    private readonly List<string> _geoProxy;
    private readonly List<string> _geoDirect;
    private readonly bool _enableGeo;
    private readonly bool _enableGfwList;
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly ProxyMetrics _metrics = new();
    private int _ipv6PacketLogCount = 0; // 用于限制 IPv6 日志输出
    private DateTime _lastNonTcpUdpLogTime = DateTime.MinValue; // 用于限制非 TCP/UDP 日志输出
    private readonly ConcurrentDictionary<string, TcpRelayState> _relayStates = new();
    private readonly List<string> _bypassedDnsServers = new(); // 已添加绕过路由的 DNS 服务器
    private readonly ConcurrentDictionary<string, int> _directBypassedIps = new(); // 直连 IP 绕过路由状态：0=添加中，1=已确认
    private const string DirectIpCacheFile = "direct_ip_cache.txt"; // 直连 IP 持久化缓存，下次启动直接加载
    private readonly ConcurrentDictionary<string, bool> _proxyBlockedIps = new(); // 代理封锁的 IP（超时/拒绝），SYN 直接 RST
    private const string BlockedIpCacheFile = "blocked_ip_cache.txt"; // 代理封锁 IP 持久化缓存
    private readonly ConcurrentDictionary<string, string> _ipHostnameCache = new(); // IP → 域名，用于 CONNECT
    private readonly TaskCompletionSource _proxyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TunProxyService(
        string proxyHost,
        int proxyPort,
        TunProxy.Core.Connections.ProxyType proxyType,
        string? username = null,
        string? password = null,
        List<string>? geoProxy = null,
        List<string>? geoDirect = null,
        string geoIpDbPath = "GeoLite2-Country.mmdb",
        bool enableGeo = false,
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
        _enableGeo = enableGeo;
        if (enableGeo)
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
            ActiveConnections = (_connectionManager?.ActiveConnections ?? 0) + (_directConnectionManager?.ActiveConnections ?? 0),
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
            Log.Debug("创建 TUN 适配器...");
            _adapter = WintunAdapter.CreateAdapter("TunProxy", "Wintun");
            Log.Debug("TUN 适配器创建成功");

            using var session = _adapter.StartSession(0x400000);
            Log.Debug("会话启动成功");

            // 3. 配置 TUN 接口 IP 和路由
            Log.Debug("配置 TUN 接口 IP...");
            ConfigureTunInterface();
            DisableIPv6OnTunInterface();

            Log.Information("TUN 接口配置完成");

            // 配置路由模式
            Log.Debug("路由模式：{Mode}", "global");

            // 在添加 TUN 默认路由之前，探测物理网卡 IP（用于后续绑定代理连接）
            // 此时 TUN 默认路由尚未生效，UDP 路由查找会使用物理网卡
            IPAddress? proxyBindAddr = ProbeLocalIpForHost(_proxyHost, _proxyPort);
            Log.Information("物理网卡探测结果（代理连接绑定地址）：{IP}", proxyBindAddr?.ToString() ?? "(失败)");

            if (true) // TODO: AutoAddDefaultRoute
            {
                // 先为代理服务器添加绕过路由，避免循环
                if (_routeService != null)
                {
                    var origGw = _routeService.GetOriginalDefaultGateway();
                    Log.Information("原始默认网关：{Gateway}", origGw ?? "(未找到)");
                    var proxyBypassOk = _routeService.AddBypassRoute(_proxyHost);
                    Log.Information("代理服务器绕过路由：{Result}（{IP}）", proxyBypassOk ? "成功" : "失败", _proxyHost);
                }

                // 为 DNS 服务器添加绕过路由（HTTP 代理通常不允许 CONNECT 到 53 端口）
                AddDnsServersBypassRoutes();

                // 加载直连 IP 缓存，立即添加绕过路由（跳过首次 SYN→RST 流程）
                LoadAndApplyDirectIpCache();

                // 加载代理封锁 IP 缓存，SYN 直接 RST（跳过超时重试）
                LoadBlockedIpCache();

                Log.Debug("添加默认路由...");
                AddDefaultRoute();
            }

            if (!_enableGeo && (_geoProxy.Count > 0 || _geoDirect.Count > 0))
            {
                Log.Information("GEO 路由已禁用（EnableGeo=false），GeoProxy/GeoDirect 配置不生效");
            }

            // 4. 创建连接管理器（代理连接管理器绑定物理网卡 IP，确保不走 TUN 接口）
            _connectionManager = new TcpConnectionManager(_proxyHost, _proxyPort, _proxyType, _username, _password,
                bindAddress: proxyBindAddr);
            _directConnectionManager = new TcpConnectionManager(string.Empty, 0, ProxyType.Direct);
            Log.Debug("连接管理器初始化完成");

            Log.Information("TunProxy 运行中，按 Ctrl+C 停止");
            Log.Information("代理目标：{Host}:{Port}", _proxyHost, _proxyPort);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // GEO/GFW 后台初始化：
            // - HTTP 代理：直接用 WebProxy 下载，无需等待 TUN（绕过企业内网 DNS 限制）
            // - SOCKS5：等待 PacketLoop 就绪后通过 TUN 下载
            string? httpProxyUrl = _proxyType == ProxyType.Http
                ? $"http://{_proxyHost}:{_proxyPort}"
                : null;

            if (_enableGeo && _geoIpService != null && (_geoProxy.Count > 0 || _geoDirect.Count > 0))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (httpProxyUrl == null)
                            await _proxyReady.Task.WaitAsync(_cts.Token); // SOCKS5：等 TUN 就绪
                        await _geoIpService.InitializeAsync(_cts.Token, httpProxyUrl);
                        Log.Information("GEO 代理规则：{Proxy}，直连规则：{Direct}",
                            string.Join(",", _geoProxy), string.Join(",", _geoDirect));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log.Warning(ex, "GeoIP 初始化失败"); }
                });
            }

            if (_enableGfwList && _gfwListService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (httpProxyUrl == null)
                            await _proxyReady.Task.WaitAsync(_cts.Token);
                        await _gfwListService.InitializeAsync(_cts.Token, httpProxyUrl);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log.Warning(ex, "GFWList 初始化失败"); }
                });
            }

            // 启动流量统计定时器（每 60 秒）
            _ = Task.Run(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
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

                    // 显示诊断信息，帮助定位流量为零的原因
                    if (snapshot.RawPacketsReceived > 0)
                    {
                        Log.Information("诊断统计：原始数据包 {Raw}，IPv6 {IPv6}，其他解析失败 {OtherParseFailed}，" +
                            "非TCP/UDP {NonTcpUdp}，端口过滤 {PortFiltered}，直连路由 {DirectRouted}，DNS查询 {Dns}",
                            snapshot.RawPacketsReceived,
                            snapshot.IPv6Packets,
                            snapshot.ParseFailures - snapshot.IPv6Packets,
                            snapshot.NonTcpUdpPackets,
                            snapshot.PortFilteredPackets,
                            snapshot.DirectRoutedPackets,
                            snapshot.DnsQueries);

                        // 如果收到了数据包但没有处理任何TCP流量，提供诊断建议
                        if (snapshot.TotalPackets == 0 && snapshot.RawPacketsReceived > 10)
                        {
                            if (snapshot.IPv6Packets > 0)
                            {
                                Log.Warning("检测到 IPv6 流量但暂不支持。IPv6 已在适配器上禁用，但系统可能仍在尝试使用 IPv6");
                            }
                            if (snapshot.NonTcpUdpPackets > 0 && snapshot.PortFilteredPackets == 0 && snapshot.DnsQueries == 0)
                            {
                                Log.Warning("只检测到非 TCP/UDP 流量（如 ICMP ping）。可能原因：");
                                Log.Warning("  1) 应用程序（如浏览器、curl）未通过 TUN 适配器发送流量");
                                Log.Warning("  2) DNS 解析可能绕过了 TUN 适配器（使用系统 DNS 缓存或 DoH）");
                                Log.Warning("  3) 路由配置可能未生效，流量走了其他网络接口");
                                Log.Warning("建议：尝试 ping 10.0.0.1 测试 TUN 连通性，或使用 'route print' 检查路由表");
                            }
                        }
                    }
                }
            }, ct);

            try
            {
                // PacketLoop 现在是同步的，需要在后台任务中运行
                var loopTask = Task.Run(() => PacketLoop(session, _cts.Token), _cts.Token);
                await loopTask;
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
            _directConnectionManager?.Dispose();
            Log.Information("连接管理器已清理");

            // 4. 删除路由
            try
            {
                _routeService?.RemoveBypassRoute(_proxyHost);
                Log.Information("代理服务器绕过路由已删除");

                // 删除 DNS 服务器绕过路由
                foreach (var dns in _bypassedDnsServers)
                {
                    _routeService?.RemoveBypassRoute(dns);
                }
                if (_bypassedDnsServers.Count > 0)
                    Log.Information("DNS 服务器绕过路由已删除（{Count} 条）", _bypassedDnsServers.Count);

                // 删除直连 IP 绕过路由
                foreach (var ip in _directBypassedIps.Keys)
                {
                    _routeService?.RemoveBypassRoute(ip);
                }
                if (_directBypassedIps.Count > 0)
                    Log.Information("直连 IP 绕过路由已删除（{Count} 条）", _directBypassedIps.Count);

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

    /// <summary>
    /// 查找 Wintun 适配器的实际网络连接名称
    /// </summary>
    private string GetTunInterfaceName()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            var tunInterface = interfaces.FirstOrDefault(iface =>
                iface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                iface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase));

            if (tunInterface != null)
            {
                Log.Debug("找到 TUN 接口：{Name} (描述: {Desc})", tunInterface.Name, tunInterface.Description);
                return tunInterface.Name;
            }

            Log.Warning("未找到 Wintun 网络接口，将使用默认名称 'TunProxy'");
            return "TunProxy";
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "无法枚举网络接口，使用默认名称");
            return "TunProxy";
        }
    }

    private void ConfigureTunInterface()
    {
        try
        {
            var actualInterfaceName = GetTunInterfaceName();
            Log.Information("使用接口名称: {Name}", actualInterfaceName);

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ip set address \"{actualInterfaceName}\" static 10.0.0.1 255.255.255.0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                var error = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);

                if (proc.ExitCode != 0)
                {
                    var outputText = output.Trim();
                    // "对象已存在" = IP 已正确配置，不是真正的错误
                    if (outputText.Contains("已存在") || outputText.Contains("already exists"))
                    {
                        Log.Debug("TUN 接口 IP 已存在，无需重新配置");
                    }
                    else
                    {
                        Log.Warning("TUN 接口配置返回错误码 {ExitCode}", proc.ExitCode);
                        if (!string.IsNullOrEmpty(error))
                        {
                            Log.Warning("错误信息：{Error}", error.Trim());
                        }
                        Log.Information("请手动运行：netsh interface ip set address \"{Interface}\" static 10.0.0.1 255.255.255.0",
                            actualInterfaceName);
                    }
                }
                else
                {
                    Log.Debug("TUN 接口配置成功");
                }

                if (!string.IsNullOrEmpty(output))
                {
                    Log.Debug("配置输出：{Output}", output.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动配置 TUN 接口失败");
            Log.Information("请手动运行：netsh interface ip set address \"<接口名称>\" static 10.0.0.1 255.255.255.0");
        }
    }

    /// <summary>
    /// 显示网络接口信息以帮助诊断
    /// </summary>
    private void ShowNetworkInterfaces()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            Log.Information("当前网络接口列表：");
            foreach (var iface in interfaces)
            {
                if (iface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase) ||
                    iface.Name.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    iface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                {
                    var ipProps = iface.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses
                        .FirstOrDefault(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                    Log.Information("  找到 TUN 接口：名称={Name}, 描述={Desc}, IP={IP}, 状态={Status}",
                        iface.Name, iface.Description, ipv4?.Address?.ToString() ?? "无", iface.OperationalStatus);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "无法列出网络接口");
        }
    }

    /// <summary>
    /// 禁用 TUN 接口的 IPv6 以避免 IPv6 流量干扰
    /// </summary>
    private void DisableIPv6OnTunInterface()
    {
        try
        {
            var actualInterfaceName = GetTunInterfaceName();

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ipv6 set interface \"{actualInterfaceName}\" forwarding=disabled advertise=disabled",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            Log.Debug("TUN 接口 IPv6 已禁用");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "禁用 IPv6 失败（可忽略）");
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
    /// 为系统 DNS 服务器添加绕过 TUN 的直连路由
    /// HTTP 代理通常拒绝 CONNECT 到 53 端口，所以 DNS 必须绕过 TUN 直连
    /// </summary>
    private void AddDnsServersBypassRoutes()
    {
        try
        {
            var dnsServers = new HashSet<string>();

            // 获取所有活动网卡的 DNS 服务器
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                foreach (var dns in ni.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !dns.ToString().StartsWith("127."))
                    {
                        dnsServers.Add(dns.ToString());
                    }
                }
            }

            // 添加常用公共 DNS（以防系统 DNS 未获取到）
            dnsServers.Add("8.8.8.8");
            dnsServers.Add("8.8.4.4");
            dnsServers.Add("1.1.1.1");
            dnsServers.Add("114.114.114.114");

            foreach (var dns in dnsServers)
            {
                if (_routeService?.AddBypassRoute(dns) == true)
                {
                    _bypassedDnsServers.Add(dns);
                    Log.Debug("DNS 绕过路由已添加：{DNS}", dns);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "添加 DNS 绕过路由失败");
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

    /// <summary>
    /// 启动时加载直连 IP 缓存并批量添加绕过路由
    /// </summary>
    private void LoadAndApplyDirectIpCache()
    {
        if (!File.Exists(DirectIpCacheFile)) return;
        try
        {
            var ips = File.ReadAllLines(DirectIpCacheFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
            if (ips.Count == 0) return;

            int added = 0;
            foreach (var ip in ips)
            {
                if (_directBypassedIps.TryAdd(ip, 0)) // pending
                {
                    if (_routeService?.AddBypassRoute(ip, 24) == true) // 缓存条目均为 /24 子网
                    {
                        _directBypassedIps.TryUpdate(ip, 1, 0); // confirmed
                        added++;
                    }
                    else
                        _directBypassedIps.TryRemove(ip, out _);
                }
            }
            Log.Information("直连 IP 缓存加载：{Total} 条记录，成功添加 {Added} 条绕过路由", ips.Count, added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载直连 IP 缓存失败，将在运行时重建");
        }
    }

    /// <summary>
    /// 追加新直连 IP 到缓存文件（仅追加，不重写）
    /// </summary>
    private static void AppendDirectIpCache(string ip)
    {
        try { File.AppendAllText(DirectIpCacheFile, ip + Environment.NewLine); }
        catch { /* 写失败不影响功能 */ }
    }

    /// <summary>
    /// 探测到达目标主机时本机使用的物理网卡 IP（通过 UDP Socket 触发路由查找）
    /// </summary>
    private static IPAddress? ProbeLocalIpForHost(string host, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, port);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (Exception ex)
        {
            Log.Warning("物理网卡 IP 探测失败：{Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 取 IPv4 的 /24 子网地址（如 106.11.43.246 → 106.11.43.0）
    /// </summary>
    private static string GetNet24(string ip)
    {
        var p = ip.Split('.');
        return p.Length == 4 ? $"{p[0]}.{p[1]}.{p[2]}.0" : ip;
    }

    /// <summary>
    /// 启动时加载代理封锁 IP 缓存（这些 IP 的 SYN 直接 RST，不尝试代理连接）
    /// </summary>
    private void LoadBlockedIpCache()
    {
        if (!File.Exists(BlockedIpCacheFile)) return;
        try
        {
            var ips = File.ReadAllLines(BlockedIpCacheFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct()
                .ToList();
            foreach (var ip in ips)
                _proxyBlockedIps.TryAdd(ip, true);
            if (ips.Count > 0)
                Log.Information("代理封锁 IP 缓存加载：{Count} 条", ips.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载代理封锁 IP 缓存失败");
        }
    }

    /// <summary>
    /// 追加新封锁 IP 到缓存文件
    /// </summary>
    private static void AppendBlockedIpCache(string ip)
    {
        try { File.AppendAllText(BlockedIpCacheFile, ip + Environment.NewLine); }
        catch { /* 写失败不影响功能 */ }
    }

    private void PacketLoop(WintunSession session, CancellationToken ct)
    {
        // 通知后台任务（GEO/GFW 下载等）PacketLoop 已就绪，可以通过 TUN 代理发起请求
        _proxyReady.TrySetResult();

        var readWaitEvent = session.ReadWaitEvent;

        // 启动独立的清理任务，避免阻塞数据包接收循环
        _ = Task.Run(async () =>
        {
            var cleanupTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await cleanupTimer.WaitForNextTickAsync(ct))
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

                    // 记录收到的原始数据包
                    _metrics.IncrementRawPacketsReceived();

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
            {
                _metrics.IncrementParseFailures();

                // 添加诊断日志以了解解析失败的原因
                if (data.Length >= 1)
                {
                    byte versionAndIhl = data[0];
                    byte version = (byte)((versionAndIhl >> 4) & 0x0F);
                    if (version == 6)
                    {
                        _metrics.IncrementIPv6Packets();
                        // 只记录前 3 个 IPv6 数据包，避免日志刷屏
                        if (Interlocked.Increment(ref _ipv6PacketLogCount) <= 3)
                        {
                            Log.Debug("收到 IPv6 数据包（暂不支持），长度：{Length}", data.Length);
                            if (_ipv6PacketLogCount == 3)
                            {
                                Log.Information("IPv6 数据包将被忽略，不再记录详细日志");
                            }
                        }
                    }
                    else if (version != 4)
                    {
                        Log.Debug("收到未知 IP 版本数据包：版本 {Version}，长度：{Length}", version, data.Length);
                    }
                    else
                    {
                        Log.Debug("IPv4 数据包解析失败，长度：{Length}（可能头部不完整）", data.Length);
                    }
                }
                else
                {
                    Log.Debug("收到空数据包");
                }
                return;
            }

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
            {
                _metrics.IncrementNonTcpUdpPackets();

                // 每 10 秒记录一次非 TCP/UDP 数据包的详细信息，帮助诊断
                var now = DateTime.UtcNow;
                if ((now - _lastNonTcpUdpLogTime).TotalSeconds >= 10)
                {
                    _lastNonTcpUdpLogTime = now;
                    var protocol = packet.Header.ProtocolType.ToString();
                    Log.Debug("收到非 TCP/UDP 数据包：协议 {Protocol}, {Source} -> {Dest}",
                        protocol, packet.Header.SourceAddress, packet.Header.DestinationAddress);
                }
                return;
            }

            var destPort = packet.DestinationPort.Value;
            var sourcePort = packet.SourcePort.Value;
            var destIP = packet.Header.DestinationAddress.ToString();

            if (packet.TCPHeader.HasValue)
            {
                var logFlags = packet.TCPHeader.Value;
                if (logFlags.SYN && !logFlags.ACK)
                {
                    // TCP SYN 包 - 新连接尝试
                    Log.Debug("收到 TCP SYN：{Source}:{SrcPort} -> {Dest}:{DestPort}",
                        packet.Header.SourceAddress, sourcePort, destIP, destPort);
                }
            }

            // 纯 ACK 包（无负载、非 SYN/FIN/RST）直接忽略
            if (packet.TCPHeader.HasValue)
            {
                var f = packet.TCPHeader.Value;
                if (packet.Payload.Length == 0 && !f.SYN && !f.FIN && !f.RST)
                    return;
            }

            // TunProxy 自身连接到代理服务器的数据包进入 TUN → 说明绕过路由失效，静默丢弃避免影响自身连接
            // （正常情况下 /32 绕过路由应拦截这些包，使其走物理网卡）
            if (destIP == _proxyHost && destPort == _proxyPort)
            {
                Log.Warning("检测到代理服务器流量进入 TUN（绕过路由可能失效）：{IP}:{Port}，静默丢弃", destIP, destPort);
                return;
            }

            // 只代理 HTTP (80) 和 HTTPS (443)
            if (destPort != 80 && destPort != 443)
            {
                _metrics.IncrementPortFilteredPackets();
                Log.Debug("TCP 数据包端口过滤：{Source}:{SrcPort} -> {Dest}:{DestPort}（仅支持 80/443）",
                    packet.Header.SourceAddress, sourcePort, destIP, destPort);
                // 对 SYN 包回 RST，阻止客户端无限重传
                if (packet.TCPHeader.HasValue && packet.TCPHeader.Value.SYN)
                    await WriteTcpRstResponseAsync(session, packet, ct);
                return;
            }

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
            if (_enableGeo && _geoIpService != null && (_geoProxy.Count > 0 || _geoDirect.Count > 0))
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
                _metrics.IncrementDirectRoutedPackets();
            }

            var connectionManager = shouldProxy ? _connectionManager : _directConnectionManager;
            if (connectionManager == null)
            {
                Log.Warning("连接管理器未初始化");
                return;
            }

            var connKey = MakeConnKey(packet);
            var tcpFlags = packet.TCPHeader!.Value;

            // ── SYN：只建中继状态和 SYN-ACK，不占连接池位
            // 连接池条目推迟到首个数据包时才创建，避免大量 SYN 快速填满连接池
            if (tcpFlags.SYN && !tcpFlags.ACK)
            {
                // 直连 IP 处理（GEO=CN 等）：用 /24 路由覆盖整段，减少路由表膨胀（约 1/256 条目数）
                // - pending(0)：首次见到，路由添加中，RST 让客户端 ~1s 后重试
                // - confirmed(1)：路由已确认，RST → OS 直接用真实网卡，零 TUN 开销
                // 注意：不能让直连 IP 走 _directConnectionManager，路由未就绪时会死循环
                if (!shouldProxy && _routeService != null)
                {
                    var net24 = GetNet24(destIP); // 取 /24 前缀（如 106.11.43.246 → 106.11.43.0）
                    if (_directBypassedIps.TryAdd(net24, 0)) // 首次：pending
                    {
                        var subnet = net24;
                        _ = Task.Run(() =>
                        {
                            if (_routeService.AddBypassRoute(subnet, 24)) // /24 路由
                            {
                                _directBypassedIps.TryUpdate(subnet, 1, 0); // confirmed
                                AppendDirectIpCache(subnet);
                            }
                            else
                                _directBypassedIps.TryRemove(subnet, out _);
                        });
                    }
                    // 无论 pending 还是 confirmed，一律 RST
                    await WriteTcpRstResponseAsync(session, packet, ct);
                    return;
                }

                // 代理封锁 IP：SYN 直接 RST，避免浪费时间等超时
                if (_proxyBlockedIps.ContainsKey(destIP))
                {
                    await WriteTcpRstResponseAsync(session, packet, ct);
                    return;
                }

                await WriteSynAckResponseAsync(session, packet, out var serverIsn, ct);
                var relayState = new TcpRelayState(packet, serverIsn, packet.TCPHeader.Value.SequenceNumber);
                _relayStates.TryAdd(connKey, relayState);
                return;
            }

            // ── 非 SYN：只查找已有连接，绝不新建 ────────────────────────────
            // 若没有对应的中继状态，说明连接已关闭或从未建立
            if (!_relayStates.TryGetValue(connKey, out var state))
            {
                if (tcpFlags.FIN || tcpFlags.RST)
                {
                    // 清理可能残留的连接池条目
                    connectionManager.RemoveConnectionByKey(connKey);
                    Log.Debug("收到孤立 FIN/RST，忽略：{Key}", connKey);
                }
                else if (packet.Payload.Length > 0)
                {
                    // 数据包到达但连接已关闭，回 RST
                    await WriteTcpRstResponseAsync(session, packet, ct);
                    Log.Debug("收到孤立数据包，回 RST：{Key}", connKey);
                }
                return;
            }

            var existingConn = connectionManager.GetExistingConnection(packet);
            _ = existingConn; // 不再直接使用，由 PSH 分支内部按需获取

            // ── FIN：客户端关闭连接 ──────────────────────────────────────────
            if (tcpFlags.FIN)
            {
                Log.Debug("客户端 FIN：{Key}", connKey);
                _relayStates.TryRemove(connKey, out _);
                (state.ConnectionManager ?? connectionManager).RemoveConnectionByKey(connKey);
                if (state.IsProxyConnected) _metrics.DecrementActiveConnections();
                return;
            }

            // ── RST：客户端重置连接 ──────────────────────────────────────────
            if (tcpFlags.RST)
            {
                _relayStates.TryRemove(connKey, out _);
                (state.ConnectionManager ?? connectionManager).RemoveConnectionByKey(connKey);
                if (state.IsProxyConnected) _metrics.DecrementActiveConnections();
                Log.Debug("客户端 RST：{Key}", connKey);
                return;
            }

            // ── PSH+ACK / 数据包：转发到代理 ────────────────────────────────
            if (packet.Payload.Length > 0)
            {
                TcpConnection? conn;

                // 首包：延迟连接代理（此时能提取 SNI/Host，避免用 IP 做 CONNECT）
                if (!state.IsProxyConnected)
                {
                    // Interlocked 保证只有一个线程做连接，其他并发包等重传
                    if (Interlocked.CompareExchange(ref state.ConnectStarted, 1, 0) != 0)
                        return;

                    _metrics.IncrementTotalConnections();
                    _metrics.IncrementActiveConnections();

                    // 提取真实域名（SNI 或 HTTP Host），决定路由
                    string connectHost;
                    if (destPort == 443)
                        connectHost = ExtractSni(packet.Payload)
                            ?? (_ipHostnameCache.TryGetValue(destIP, out var h1) ? h1 : destIP);
                    else
                        connectHost = ExtractHttpHost(packet.Payload)
                            ?? (_ipHostnameCache.TryGetValue(destIP, out var h2) ? h2 : destIP);

                    // 基于域名重新判断路由（GFWList 路由）
                    if (_enableGfwList && _gfwListService != null && connectHost != destIP)
                    {
                        bool gfwShouldProxy = _gfwListService.IsInGfwList(connectHost);
                        if (gfwShouldProxy != shouldProxy)
                        {
                            Log.Debug("GFWList 路由覆盖：{Host} → {Mode}", connectHost, gfwShouldProxy ? "代理" : "直连");
                            connectionManager = gfwShouldProxy ? _connectionManager! : _directConnectionManager!;
                        }
                    }

                    if (connectHost != destIP)
                        Log.Debug("CONNECT 使用域名：{Host}（IP：{IP}）", connectHost, destIP);

                    // 首包时才占连接池位
                    conn = connectionManager.GetOrCreateConnection(packet);
                    if (conn == null)
                    {
                        _metrics.DecrementActiveConnections();
                        _metrics.IncrementFailedConnections();
                        _relayStates.TryRemove(connKey, out _);
                        Log.Warning("连接池已满：{IP}:{Port}", destIP, destPort);
                        await WriteTcpRstResponseAsync(session, packet, ct);
                        return;
                    }

                    try
                    {
                        await conn.ConnectAsync(connectHost, destPort, ct);
                        state.IsProxyConnected = true;
                        state.ConnectionManager = connectionManager; // 记住用了哪个池，后续包复用
                        Log.Information("连接建立：{Host}:{Port}", connectHost, destPort);
                        _ = Task.Run(() => RelayProxyToTunAsync(session, conn, connKey, connectionManager, ct), ct);
                    }
                    catch (Exception ex)
                    {
                        _metrics.DecrementActiveConnections();
                        _metrics.IncrementFailedConnections();
                        _relayStates.TryRemove(connKey, out _);
                        connectionManager.RemoveConnectionByKey(connKey);
                        Log.Warning(ex, "连接建立失败：{IP}:{Port}", destIP, destPort);
                        // 只有代理明确拒绝（4xx/5xx）才加入封锁缓存
                        // 超时（OperationCanceledException）是临时性的，不能永久封锁
                        if (ex.Message.Contains("PROXY_DENIED") &&
                            _proxyBlockedIps.TryAdd(destIP, true))
                        {
                            Log.Debug("代理封锁 IP 已记录：{IP}（后续 SYN 直接 RST）", destIP);
                            _ = Task.Run(() => AppendBlockedIpCache(destIP));
                        }
                        await WriteTcpRstResponseAsync(session, packet, ct);
                        return;
                    }
                }
                else
                {
                    // 已连接：必须用首包时确定的连接池（可能被 GFWList 覆盖），不能重新评估
                    conn = (state.ConnectionManager ?? connectionManager).GetExistingConnection(packet);
                    if (conn == null) return; // 连接已被清理
                }

                uint incomingSeq = tcpFlags.SequenceNumber;
                uint incomingEnd = incomingSeq + (uint)packet.Payload.Length;

                // 重传检测：若此数据已被确认，不重发给代理
                if (IsSeqBeforeOrEqual(incomingEnd, state.ExpectedClientSeq))
                {
                    await WriteAckToTunAsync(session, packet, state.NextServerSeq, state.ExpectedClientSeq, ct);
                    Log.Debug("重传包，丢弃不转发，回 ACK：seq={Seq} end={End} expected={Exp}",
                        incomingSeq, incomingEnd, state.ExpectedClientSeq);
                    return;
                }

                state.ExpectedClientSeq = incomingEnd;

                try
                {
                    await conn.SendAsync(packet.Payload, ct);
                    _metrics.AddBytesSent(packet.Payload.Length);
                    await WriteAckToTunAsync(session, packet, state.NextServerSeq, state.ExpectedClientSeq, ct);
                }
                catch (Exception ex) when (ex is System.IO.IOException or SocketException or ObjectDisposedException)
                {
                    _relayStates.TryRemove(connKey, out _);
                    connectionManager.RemoveConnectionByKey(connKey);
                    _metrics.DecrementActiveConnections();
                    Log.Debug("发送数据失败（连接已断开）：{IP}:{Port}", destIP, destPort);
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is ObjectDisposedException)
                return; // 服务停止时 WintunSession 已释放，正常情况
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
    /// 发送 SYN-ACK 响应到 TUN 设备
    /// </summary>
    private static unsafe Task WriteSynAckResponseAsync(WintunSession session, IPPacket requestPacket, out uint serverIsn, CancellationToken ct)
    {
        var responsePacket = BuildSynAckPacket(requestPacket, out serverIsn);

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

    private static unsafe Task WriteTcpRstResponseAsync(WintunSession session, IPPacket requestPacket, CancellationToken ct)
    {
        var responsePacket = BuildTcpRstPacket(requestPacket);

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
    /// TCP 序列号比较（含回绕）：seq 是否 <= boundary
    /// </summary>
    private static bool IsSeqBeforeOrEqual(uint seq, uint boundary) =>
        (int)(boundary - seq) >= 0;

    /// <summary>
    /// 发送纯 ACK 到 TUN 客户端（flags=0x10，无载荷）
    /// </summary>
    private static Task WriteAckToTunAsync(WintunSession session, IPPacket requestPacket,
        uint seqNum, uint ackNum, CancellationToken ct)
    {
        var packet = BuildAckPacket(requestPacket, seqNum, ackNum);
        if (packet.Length == 0) return Task.CompletedTask;

        var sendPacket = session.AllocateSendPacket((uint)packet.Length);
        if (sendPacket == IntPtr.Zero) return Task.CompletedTask;

        System.Runtime.InteropServices.Marshal.Copy(packet, 0, sendPacket, packet.Length);
        session.SendPacket(sendPacket);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 构建纯 ACK 包（flags=0x10，无数据）
    /// </summary>
    private static byte[] BuildAckPacket(IPPacket requestPacket, uint seqNum, uint ackNum)
    {
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();
        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        var tcpHeader = new byte[20];
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(2, 2), destPort);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), seqNum);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);
        tcpHeader[12] = 0x50;
        tcpHeader[13] = 0x10; // ACK only
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), 65535);

        var ipHeader = new byte[20];
        ipHeader[0] = 0x45;
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), 40);
        ipHeader[6] = 0x40;
        ipHeader[8] = 0x40;
        ipHeader[9] = 0x06;
        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);
        var ipChecksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), ipChecksum);

        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 6, tcpHeader);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);

        var packet = new byte[40];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpHeader, 0, packet, 20, 20);
        return packet;
    }

    /// <summary>
    /// 发送 FIN-ACK 响应到 TUN 设备（代理关闭连接时通知客户端）
    /// </summary>
    private static Task WriteFinAckResponseAsync(WintunSession session, IPPacket synPacket,
        uint seqNum, uint ackNum, CancellationToken ct)
    {
        var responsePacket = BuildFinAckPacket(synPacket, seqNum, ackNum);
        if (responsePacket.Length == 0) return Task.CompletedTask;

        var sendPacket = session.AllocateSendPacket((uint)responsePacket.Length);
        if (sendPacket == IntPtr.Zero) return Task.CompletedTask;

        System.Runtime.InteropServices.Marshal.Copy(responsePacket, 0, sendPacket, responsePacket.Length);
        session.SendPacket(sendPacket);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 构建 FIN-ACK 包
    /// </summary>
    private static byte[] BuildFinAckPacket(IPPacket synPacket, uint seqNum, uint ackNum)
    {
        var sourceIP = synPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = synPacket.Header.SourceAddress.GetAddressBytes();
        var sourcePort = synPacket.DestinationPort!.Value;
        var destPort = synPacket.SourcePort!.Value;

        var tcpHeader = new byte[20];
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(2, 2), destPort);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), seqNum);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);
        tcpHeader[12] = 0x50;
        tcpHeader[13] = 0x11; // FIN + ACK
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), 0);

        var ipHeader = new byte[20];
        ipHeader[0] = 0x45;
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), 40);
        ipHeader[6] = 0x40;
        ipHeader[8] = 0x40;
        ipHeader[9] = 0x06;
        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);
        var ipChecksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), ipChecksum);

        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 6, tcpHeader);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);

        var packet = new byte[40];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpHeader, 0, packet, 20, 20);
        return packet;
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
    /// 构建 SYN-ACK 响应包
    /// </summary>
    private static byte[] BuildSynAckPacket(IPPacket requestPacket, out uint serverIsn)
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

        // 序列号：使用随机初始序列号
        uint seqNum = (uint)Random.Shared.Next();
        serverIsn = seqNum;
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), seqNum);

        // 确认号：客户端序列号 + 1（SYN 消耗 1 个序列号）
        uint ackNum = requestPacket.TCPHeader.HasValue
            ? requestPacket.TCPHeader.Value.SequenceNumber + 1
            : 0;
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);

        // 数据偏移（5 * 4 = 20字节）和标志位
        tcpHeader[12] = 0x50;  // 数据偏移 = 5 (20字节)
        tcpHeader[13] = 0x12;  // SYN + ACK 标志 (0x02 + 0x10 = 0x12)

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

        // 总长度（IP头 + TCP头，无数据）
        var totalLength = (ushort)(20 + 20);
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

        // 计算 TCP 校验和（SYN-ACK 无数据）
        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(
            sourceIP,
            destIP,
            6,  // TCP 协议
            tcpHeader);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);

        // 组装最终数据包
        var packet = new byte[40];  // IP头(20) + TCP头(20)
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpHeader, 0, packet, 20, 20);

        return packet;
    }

    /// <summary>
    /// 构建 TCP RST 响应包
    /// </summary>
    private static byte[] BuildTcpRstPacket(IPPacket requestPacket)
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

        // 序列号：0（对于 RST 响应）
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), 0);

        // 确认号：客户端序列号 + 1
        uint ackNum = requestPacket.TCPHeader.HasValue
            ? requestPacket.TCPHeader.Value.SequenceNumber + 1
            : 0;
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);

        // 数据偏移（5 * 4 = 20字节）和标志位
        tcpHeader[12] = 0x50;  // 数据偏移 = 5 (20字节)
        tcpHeader[13] = 0x14;  // RST + ACK 标志 (0x04 + 0x10 = 0x14)

        // 窗口大小
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), 0);

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

        // 总长度（IP头 + TCP头，无数据）
        var totalLength = (ushort)(20 + 20);
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

        // 计算 TCP 校验和
        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(
            sourceIP,
            destIP,
            6,  // TCP 协议
            tcpHeader);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);

        // 组装最终数据包
        var packet = new byte[40];  // IP头(20) + TCP头(20)
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpHeader, 0, packet, 20, 20);

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

            // 解析 DNS 响应，缓存 IP → 域名映射（用于后续 CONNECT 时传递正确主机名）
            try
            {
                var dnsRespPacket = DnsPacket.Parse(dnsResponse);
                if (dnsRespPacket != null)
                {
                    foreach (var answer in dnsRespPacket.Answers)
                    {
                        if (answer.Type == 1 && answer.Data.Length == 4) // A 记录
                        {
                            var ip = new IPAddress(answer.Data).ToString();
                            _ipHostnameCache.TryAdd(ip, domain);
                            Log.Debug("DNS缓存：{IP} → {Domain}", ip, domain);
                        }
                    }
                }
            }
            catch { /* 缓存失败不影响正常 DNS 响应 */ }

            // 构建 UDP 响应包
            await WriteUdpResponseToTunAsync(session, requestPacket, dnsResponse, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理 DNS 查询失败");
        }
    }

    /// <summary>
    /// 持续从代理读取响应并转发到 TUN（每个 TCP 连接一个后台任务）
    /// </summary>
    private async Task RelayProxyToTunAsync(
        WintunSession session, TcpConnection connection,
        string connKey, TcpConnectionManager connectionManager, CancellationToken ct)
    {
        const int BufferSize = 16384;
        var buffer = new byte[BufferSize];
        Log.Debug("中继任务启动：{Key}", connKey);
        bool normalClose = false;
        try
        {
            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                int bytesRead;
                try
                {
                    // 每次读取设置 2 分钟超时，防止空闲连接永久悬挂
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromMinutes(2));
                    bytesRead = await connection.ReceiveAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Debug(ex, "中继读取结束：{Key}", connKey);
                    break;
                }

                if (bytesRead == 0)
                {
                    Log.Debug("代理连接关闭：{Key}", connKey);
                    normalClose = true;
                    break;
                }

                if (!_relayStates.TryGetValue(connKey, out var state))
                    break;

                _metrics.AddBytesReceived(bytesRead);
                Log.Debug("中继 {Bytes} bytes → TUN，连接：{Key}", bytesRead, connKey);

                await WriteRelayResponseToTunAsync(
                    session, state.SynPacket, buffer.AsSpan(0, bytesRead),
                    state.NextServerSeq, state.ExpectedClientSeq, ct);

                state.NextServerSeq += (uint)bytesRead;
            }
        }
        finally
        {
            // 通知客户端连接已关闭（FIN-ACK），避免客户端无限重传
            if (normalClose && _relayStates.TryGetValue(connKey, out var finalState))
            {
                try
                {
                    await WriteFinAckResponseAsync(session, finalState.SynPacket,
                        finalState.NextServerSeq, finalState.ExpectedClientSeq, ct);
                }
                catch { /* 发送 FIN 失败可忽略 */ }
            }

            _relayStates.TryRemove(connKey, out _);
            // 从连接池移除死连接，防止后续包复用它
            connectionManager.RemoveConnectionByKey(connKey);
            _metrics.DecrementActiveConnections();
            Log.Debug("中继任务结束：{Key}", connKey);
        }
    }

    /// <summary>
    /// 生成连接唯一键（与 TcpConnectionManager 保持一致）
    /// </summary>
    private static string MakeConnKey(IPPacket packet) =>
        $"{packet.Header.SourceAddress}:{packet.SourcePort}-{packet.Header.DestinationAddress}:{packet.DestinationPort}";

    /// <summary>
    /// 将中继响应写回 TUN（使用明确的序列号）
    /// </summary>
    private static Task WriteRelayResponseToTunAsync(
        WintunSession session, IPPacket synPacket, ReadOnlySpan<byte> responseData,
        uint serverSeq, uint clientAck, CancellationToken ct)
    {
        var responsePacket = BuildResponsePacketWithSeq(synPacket, responseData, serverSeq, clientAck);
        if (responsePacket.Length == 0) return Task.CompletedTask;

        var sendPacket = session.AllocateSendPacket((uint)responsePacket.Length);
        if (sendPacket == IntPtr.Zero)
        {
            Log.Warning("分配发送缓冲区失败（中继）");
            return Task.CompletedTask;
        }

        System.Runtime.InteropServices.Marshal.Copy(responsePacket, 0, sendPacket, responsePacket.Length);
        session.SendPacket(sendPacket);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 构建 TCP 响应包（使用明确的序列号，供中继任务使用）
    /// </summary>
    private static byte[] BuildResponsePacketWithSeq(
        IPPacket requestPacket, ReadOnlySpan<byte> responseData,
        uint seqNum, uint ackNum)
    {
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();
        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        var tcpHeader = new byte[20];
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(0, 2), sourcePort);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(2, 2), destPort);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(4, 4), seqNum);
        NetworkHelper.WriteUInt32BigEndian(tcpHeader.AsSpan(8, 4), ackNum);
        tcpHeader[12] = 0x50;
        tcpHeader[13] = 0x18; // PSH + ACK
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(14, 2), 65535);

        var ipHeader = new byte[20];
        ipHeader[0] = 0x45;
        ipHeader[1] = 0x00;
        var totalLength = (ushort)(20 + 20 + responseData.Length);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), totalLength);
        ipHeader[6] = 0x40;
        ipHeader[8] = 0x40;
        ipHeader[9] = 0x06;
        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);
        var ipChecksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), ipChecksum);

        var tcpSegment = new byte[20 + responseData.Length];
        Array.Copy(tcpHeader, 0, tcpSegment, 0, 20);
        responseData.CopyTo(tcpSegment.AsSpan(20));
        var tcpChecksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 6, tcpSegment);
        NetworkHelper.WriteUInt16BigEndian(tcpHeader.AsSpan(16, 2), tcpChecksum);
        Array.Copy(tcpHeader, 16, tcpSegment, 16, 2);

        var packet = new byte[20 + 20 + responseData.Length];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpSegment, 0, packet, 20, tcpSegment.Length);
        return packet;
    }

    /// <summary>
    /// 通过代理查询 DNS（支持 HTTP CONNECT 和 SOCKS5 两种代理协议）
    /// </summary>
    private async Task<byte[]?> QueryDnsViaSocks5Async(string domain, string dnsServer, CancellationToken ct)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            await client.ConnectAsync(_proxyHost, _proxyPort, ct);
            var stream = client.GetStream();

            if (_proxyType == ProxyType.Http)
            {
                // HTTP 代理：使用 CONNECT 方法建立到 DNS 服务器的隧道
                var connectReq = $"CONNECT {dnsServer}:53 HTTP/1.1\r\nHost: {dnsServer}:53\r\nProxy-Connection: Keep-Alive\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(connectReq), ct);

                var httpBuf = new byte[4096];
                var httpSb = new System.Text.StringBuilder();
                while (true)
                {
                    var n = await stream.ReadAsync(httpBuf, ct);
                    if (n == 0) { Log.Warning("HTTP 代理连接 DNS 服务器时关闭连接"); return null; }
                    httpSb.Append(System.Text.Encoding.UTF8.GetString(httpBuf, 0, n));
                    var r = httpSb.ToString();
                    if (r.Contains("\r\n\r\n"))
                    {
                        if (!r.Split('\r')[0].Contains("200")) { Log.Warning("HTTP 代理拒绝连接 DNS：{Status}", r.Split('\r')[0]); return null; }
                        break;
                    }
                }
                // 隧道建立完成，后续直接走 DNS-over-TCP
            }
            else
            {
                // SOCKS5 握手
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
                var response = new byte[2];
                await stream.ReadExactlyAsync(response, ct);

                if (response[0] != 0x05 || response[1] != 0x00)
                {
                    Log.Warning("SOCKS5 握手失败");
                    return null;
                }

                // SOCKS5 连接 DNS 服务器 TCP 53
                var connectRequest = new List<byte> { 0x05, 0x01, 0x00, 0x01 };
                var dnsIp = IPAddress.Parse(dnsServer);
                connectRequest.AddRange(dnsIp.GetAddressBytes());
                connectRequest.Add(0x00);  // 端口 53 高字节
                connectRequest.Add(0x35);  // 端口 53 低字节

                await stream.WriteAsync(connectRequest.ToArray(), ct);

                var connectResponse = new byte[256];
                var bytesRead = await stream.ReadAsync(connectResponse, ct);

                if (bytesRead < 10 || connectResponse[1] != 0x00)
                {
                    Log.Warning("SOCKS5 连接 DNS 服务器失败");
                    return null;
                }
            }

            // 构建 DNS A 记录查询（DNS-over-TCP，前缀 2 字节长度）
            var dnsQuery = new DnsPacket
            {
                TransactionId = (ushort)Random.Shared.Next(0, 65536),
                Flags = new DnsFlags(0x0100),
                Questions = new List<DnsQuestion>
                {
                    new DnsQuestion { Name = domain, Type = 1, Class = 1 }
                }
            };
            var queryBytes = dnsQuery.Build();
            var tcpQuery = new byte[queryBytes.Length + 2];
            NetworkHelper.WriteUInt16BigEndian(tcpQuery.AsSpan(0, 2), (ushort)queryBytes.Length);
            Array.Copy(queryBytes, 0, tcpQuery, 2, queryBytes.Length);

            await stream.WriteAsync(tcpQuery, ct);

            // 读取 DNS 响应（2 字节长度 + 响应数据）
            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, ct);
            var responseLength = NetworkHelper.ReadUInt16BigEndian(lengthBytes);

            var dnsResponseData = new byte[responseLength];
            await stream.ReadExactlyAsync(dnsResponseData, ct);

            return dnsResponseData;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DNS 查询异常（通过 {ProxyType} 代理）：{Domain}", _proxyType, domain);
            return null;
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// 从 TLS ClientHello 提取 SNI 域名
    /// </summary>
    private static string? ExtractSni(byte[] data)
    {
        try
        {
            // TLS Record: [0]=0x16(Handshake) [1-2]=version [3-4]=len
            if (data.Length < 9 || data[0] != 0x16) return null;
            // Handshake: [5]=0x01(ClientHello) [6-8]=len
            if (data[5] != 0x01) return null;

            int pos = 9; // ClientHello body starts here
            pos += 2;    // ClientVersion
            pos += 32;   // Random
            if (pos >= data.Length) return null;

            // Session ID
            pos += 1 + data[pos];
            if (pos + 2 > data.Length) return null;

            // Cipher Suites
            int csLen = (data[pos] << 8) | data[pos + 1];
            pos += 2 + csLen;
            if (pos + 1 > data.Length) return null;

            // Compression Methods
            pos += 1 + data[pos];
            if (pos + 2 > data.Length) return null;

            // Extensions
            int extEnd = pos + 2 + ((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            while (pos + 4 <= extEnd && pos + 4 <= data.Length)
            {
                int extType = (data[pos] << 8) | data[pos + 1];
                int extLen  = (data[pos + 2] << 8) | data[pos + 3];
                pos += 4;
                if (extType == 0x0000 && pos + 5 <= data.Length) // SNI
                {
                    // list_len(2) + name_type(1) + name_len(2) + name
                    int nameLen = (data[pos + 3] << 8) | data[pos + 4];
                    if (pos + 5 + nameLen <= data.Length)
                        return System.Text.Encoding.ASCII.GetString(data, pos + 5, nameLen);
                    return null;
                }
                pos += extLen;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 从 HTTP 请求头提取 Host 域名（不含端口）
    /// </summary>
    private static string? ExtractHttpHost(byte[] data)
    {
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(data);
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = line[5..].Trim().TrimEnd('\r');
                    // 去掉端口号
                    var colon = host.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(host[(colon + 1)..], out _))
                        host = host[..colon];
                    return host;
                }
            }
        }
        catch { }
        return null;
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
        udpHeader[6] = 0;
        udpHeader[7] = 0;

        var ipHeader = new byte[20];
        ipHeader[0] = 0x45;
        ipHeader[1] = 0x00;

        var totalLength = (ushort)(20 + 8 + responseData.Length);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(2, 2), totalLength);

        ipHeader[6] = 0x40;
        ipHeader[8] = 0x40;
        ipHeader[9] = 0x11;  // UDP

        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        Array.Copy(destIP, 0, ipHeader, 16, 4);

        var ipChecksum = NetworkHelper.CalculateIPChecksum(ipHeader);
        NetworkHelper.WriteUInt16BigEndian(ipHeader.AsSpan(10, 2), ipChecksum);

        var udpSegment = new byte[8 + responseData.Length];
        Array.Copy(udpHeader, 0, udpSegment, 0, 8);
        responseData.CopyTo(udpSegment.AsSpan(8));

        var udpChecksum = NetworkHelper.CalculateTcpUdpChecksum(sourceIP, destIP, 17, udpSegment);
        NetworkHelper.WriteUInt16BigEndian(udpHeader.AsSpan(6, 2), udpChecksum);
        Array.Copy(udpHeader, 6, udpSegment, 6, 2);

        var packet = new byte[20 + 8 + responseData.Length];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(udpSegment, 0, packet, 20, udpSegment.Length);

        return packet;
    }
}

/// <summary>
/// TCP 连接中继状态（跟踪序列号，支持持续双向中继）
/// </summary>
internal class TcpRelayState
{
    public uint NextServerSeq;      // 服务端下一个要发送的序列号
    public uint ExpectedClientSeq;  // 客户端已发送数据量（用作响应中的 ACK 号）
    public readonly IPPacket SynPacket; // 原始 SYN 包（重建响应头部用）
    public bool IsProxyConnected;   // 代理连接是否已建立（延迟到首包）
    // 用于保证首包只连接一次（多个并发 PSH+ACK 竞争时）
    public int ConnectStarted;      // 0=未开始, 1=已开始（Interlocked）
    public TcpConnectionManager? ConnectionManager; // 首包时确定的连接池（代理或直连），后续包复用

    public TcpRelayState(IPPacket synPacket, uint serverIsn, uint clientIsn)
    {
        SynPacket = synPacket;
        NextServerSeq = serverIsn + 1;
        ExpectedClientSeq = clientIsn + 1;
        IsProxyConnected = false;
        ConnectStarted = 0;
    }
}

