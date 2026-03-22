using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TunProxy.Core.Connections;
using TunProxy.Core.Packets;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// TUN 代理主程序
/// </summary>
public class TunProxyService
{
    private readonly string _proxyHost;
    private readonly int _proxyPort;
    private readonly ProxyType _proxyType;
    private readonly string? _username;
    private readonly string? _password;
    private WintunAdapter? _adapter;
    private TcpConnectionManager? _connectionManager;
    private CancellationTokenSource? _cts;

    public TunProxyService(string proxyHost, int proxyPort, ProxyType proxyType, string? username = null, string? password = null)
    {
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _proxyType = proxyType;
        _username = username;
        _password = password;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        Console.WriteLine($"🥔 TunProxy 启动中...");
        Console.WriteLine($"代理：{_proxyHost}:{_proxyPort} ({_proxyType})");

        // 1. 确保 wintun.dll 存在
        await EnsureWintunDllAsync(ct);

        // 2. 创建 TUN 适配器
        Console.WriteLine("创建 TUN 适配器...");
        _adapter = WintunAdapter.CreateAdapter("TunProxy", "Wintun");
        Console.WriteLine("✓ TUN 适配器创建成功");

        using var session = _adapter.StartSession(0x400000);
        Console.WriteLine("✓ 会话启动成功");

        // 3. 配置 TUN 接口 IP
        Console.WriteLine("配置 TUN 接口 IP...");
        ConfigureTunInterface();
        Console.WriteLine("✓ TUN 接口配置完成");

        // 4. 创建连接管理器
        _connectionManager = new TcpConnectionManager(_proxyHost, _proxyPort, _proxyType, _username, _password);
        Console.WriteLine($"✓ 连接管理器初始化完成");

        Console.WriteLine($"\n🥔 TunProxy 运行中，按 Ctrl+C 停止...\n");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await PacketLoop(session, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n正在停止...");
        }
        finally
        {
            _connectionManager?.Dispose();
            _adapter?.Dispose();
        }
    }

    /// <summary>
    /// 自动下载 wintun.dll
    /// </summary>
    private static async Task EnsureWintunDllAsync(CancellationToken ct)
    {
        var wintunPath = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
        if (File.Exists(wintunPath))
        {
            Console.WriteLine("✓ wintun.dll 已存在");
            return;
        }

        Console.WriteLine("下载 wintun.dll...");
        
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
            Console.WriteLine($"✓ wintun.dll 下载完成：{wintunPath}");
            
            File.Delete(tempZip);
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "wintun*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ wintun.dll 下载失败：{ex.Message}");
            Console.WriteLine("请手动下载：https://www.wintun.net/builds/wintun-0.14.1.zip");
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
            
            psi.Arguments = "interface ip add route 0.0.0.0/0 \"TunProxy\" 10.0.0.1";
            using var proc2 = Process.Start(psi);
            proc2?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ 自动配置失败：{ex.Message}");
            Console.WriteLine("请手动运行：");
            Console.WriteLine("  netsh interface ip set address \"TunProxy\" static 10.0.0.1 255.255.255.0");
        }
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
                Console.WriteLine($"[清理] 活跃连接数：{_connectionManager?.ActiveConnections ?? 0}");
            }

            var packet = session.ReceivePacket(out var packetSize);

            if (packet != IntPtr.Zero)
            {
                try
                {
                    var data = new byte[packetSize];
                    Marshal.Copy(packet, data, 0, (int)packetSize);
                    _ = ProcessPacketAsync(session, data, ct);
                }
                finally
                {
                    session.ReleaseReceivePacket(packet);
                }
            }
            else if (Marshal.GetLastWin32Error() == WintunNative.ERROR_NO_MORE_ITEMS)
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

            // 只处理 TCP 流量
            if (!packet.IsTCP || packet.SourcePort == null || packet.DestinationPort == null)
                return;

            var destPort = packet.DestinationPort.Value;
            
            // 只代理 HTTP (80) 和 HTTPS (443)
            if (destPort != 80 && destPort != 443)
                return;

            var destIP = packet.Header.DestinationAddress.ToString();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {packet.Header.SourceAddress}:{packet.SourcePort} -> {destIP}:{destPort} ({packet.Payload.Length} bytes)");

            // 获取或创建连接
            var connection = _connectionManager!.GetOrCreateConnection(packet);
            
            // 如果连接未建立，先连接
            if (!connection.IsConnected)
            {
                await connection.ConnectAsync(destIP, destPort, ct);
                Console.WriteLine($"  ✓ 连接建立：{destIP}:{destPort}");
            }

            // 发送数据到代理
            if (packet.Payload.Length > 0)
            {
                await connection.SendAsync(packet.Payload, ct);
                
                // 接收响应
                var responseBuffer = new byte[4096];
                var bytesRead = await connection.ReceiveAsync(responseBuffer, ct);
                
                if (bytesRead > 0)
                {
                    Console.WriteLine($"  ✓ 收到响应 {bytesRead} bytes");
                    
                    // 回写到 TUN 设备
                    await WriteResponseToTunAsync(session, packet, responseBuffer.AsSpan(0, bytesRead), ct);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 处理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 将响应数据包写回 TUN 设备
    /// </summary>
    private static unsafe Task WriteResponseToTunAsync(WintunSession session, IPPacket requestPacket, ReadOnlySpan<byte> responseData, CancellationToken ct)
    {
        // 构造响应 IP 包（交换源和目标）
        var responsePacket = BuildResponsePacket(requestPacket, responseData);
        
        if (responsePacket.Length == 0)
            return Task.CompletedTask;

        // 分配发送缓冲区
        var sendPacket = session.AllocateSendPacket((uint)responsePacket.Length);
        if (sendPacket == IntPtr.Zero)
        {
            Console.WriteLine("  ⚠ 分配发送缓冲区失败");
            return Task.CompletedTask;
        }

        try
        {
            // 复制数据到发送缓冲区
            Marshal.Copy(responsePacket, 0, sendPacket, responsePacket.Length);
            session.SendPacket(sendPacket);
        }
        finally
        {
            // 释放发送缓冲区（Wintun 会自动管理）
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 构建响应 IP 包
    /// </summary>
    private static byte[] BuildResponsePacket(IPPacket requestPacket, ReadOnlySpan<byte> responseData)
    {
        // 交换源和目标 IP
        var sourceIP = requestPacket.Header.DestinationAddress.GetAddressBytes();
        var destIP = requestPacket.Header.SourceAddress.GetAddressBytes();
        
        // 交换源和目标端口
        var sourcePort = requestPacket.DestinationPort!.Value;
        var destPort = requestPacket.SourcePort!.Value;

        // TCP 头部（20 字节）
        var tcpHeader = new byte[20];
        
        // 源端口（小端）
        tcpHeader[0] = (byte)(sourcePort & 0xFF);
        tcpHeader[1] = (byte)(sourcePort >> 8);
        
        // 目标端口（小端）
        tcpHeader[2] = (byte)(destPort & 0xFF);
        tcpHeader[3] = (byte)(destPort >> 8);
        
        // 序列号、确认号等（简化处理）
        tcpHeader[4] = 0x00;
        tcpHeader[5] = 0x00;
        tcpHeader[6] = 0x00;
        tcpHeader[7] = 0x01;
        tcpHeader[8] = 0x00;
        tcpHeader[9] = 0x00;
        tcpHeader[10] = 0x00;
        tcpHeader[11] = 0x00;
        
        // 数据偏移（5 = 20 字节）+ 标志位（ACK=0x10）
        tcpHeader[12] = 0x50;
        tcpHeader[13] = 0x10;
        
        // 窗口大小
        tcpHeader[14] = 0xFF;
        tcpHeader[15] = 0xFF;
        
        // 校验和（0 = 由系统计算）
        tcpHeader[16] = 0x00;
        tcpHeader[17] = 0x00;
        
        // 紧急指针
        tcpHeader[18] = 0x00;
        tcpHeader[19] = 0x00;

        // IP 头部（20 字节）
        var ipHeader = new byte[20];
        ipHeader[0] = 0x45; // 版本 4, IHL 5
        ipHeader[1] = 0x00; // TOS
        var totalLength = (ushort)(20 + 20 + responseData.Length);
        ipHeader[2] = (byte)(totalLength & 0xFF); // 小端
        ipHeader[3] = (byte)(totalLength >> 8);
        ipHeader[4] = 0x00; // 标识
        ipHeader[5] = 0x01;
        ipHeader[6] = 0x00; // 标志 + 片偏移
        ipHeader[7] = 0x00;
        ipHeader[8] = 0x40; // TTL 64
        ipHeader[9] = 0x06; // 协议 6 (TCP)
        ipHeader[10] = 0x00; // 校验和
        ipHeader[11] = 0x00;
        
        // 源 IP
        Array.Copy(sourceIP, 0, ipHeader, 12, 4);
        // 目标 IP
        Array.Copy(destIP, 0, ipHeader, 16, 4);

        // 组合完整数据包
        var packet = new byte[20 + 20 + responseData.Length];
        Array.Copy(ipHeader, 0, packet, 0, 20);
        Array.Copy(tcpHeader, 0, packet, 20, 20);
        responseData.CopyTo(packet.AsSpan(40));

        return packet;
    }
}

public enum ProxyType
{
    Socks5,
    Http
}

public class Program
{
    public static async Task Main(string[] args)
    {
        string proxyHost = "127.0.0.1";
        int proxyPort = 7890;
        var proxyType = ProxyType.Socks5;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--proxy" or "-p":
                    if (i + 1 < args.Length)
                    {
                        var parts = args[++i].Split(':');
                        proxyHost = parts[0];
                        if (parts.Length > 1)
                            proxyPort = int.Parse(parts[1]);
                    }
                    break;
                case "--type" or "-t":
                    if (i + 1 < args.Length)
                    {
                        proxyType = args[++i].ToLower() switch
                        {
                            "socks5" => ProxyType.Socks5,
                            "http" => ProxyType.Http,
                            _ => throw new ArgumentException($"Unknown proxy type: {args[i]}")
                        };
                    }
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return;
            }
        }

        var service = new TunProxyService(proxyHost, proxyPort, proxyType);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await service.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 错误：{ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
🥔 TunProxy - .NET 8 TUN 代理（傻瓜版）

用法：TunProxy.CLI [选项]

选项:
  -p, --proxy <host:port>  代理服务器地址 (默认：127.0.0.1:7890)
  -t, --type <type>        代理类型：socks5, http (默认：socks5)
  -h, --help               显示帮助

示例:
  TunProxy.CLI -p 127.0.0.1:7890 -t socks5
  TunProxy.CLI --proxy 192.168.1.100:8080 --type http

注意：需要管理员权限运行
");
    }
}
