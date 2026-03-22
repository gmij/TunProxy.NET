using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TunProxy.Core.Packets;
using TunProxy.Core.Wintun;
using TunProxy.Proxy;

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

        using var session = _adapter.StartSession(0x400000); // 4MB ring buffer
        Console.WriteLine("✓ 会话启动成功");

        // 3. 配置 TUN 接口 IP
        Console.WriteLine("配置 TUN 接口 IP...");
        ConfigureTunInterface();
        Console.WriteLine("✓ TUN 接口配置完成");

        Console.WriteLine("\n🥔 TunProxy 运行中，按 Ctrl+C 停止...\n");

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
        
        // 下载 Wintun ZIP
        var downloadUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
        var tempZip = Path.Combine(Path.GetTempPath(), "wintun.zip");
        
        try
        {
            var data = await client.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(tempZip, data, ct);
            
            // 解压
            ZipFile.ExtractToDirectory(tempZip, Path.GetTempPath(), true);
            
            // 找到 DLL 并复制
            var dllFiles = Directory.GetFiles(Path.GetTempPath(), "wintun.dll", SearchOption.AllDirectories);
            if (dllFiles.Length == 0)
                throw new Exception("未找到 wintun.dll");
            
            File.Copy(dllFiles[0], wintunPath, true);
            Console.WriteLine($"✓ wintun.dll 下载完成：{wintunPath}");
            
            // 清理
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
            // 使用 netsh 配置 TUN 接口 IP
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
            
            // 添加路由（可选）
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

        while (!ct.IsCancellationRequested)
        {
            var packet = session.ReceivePacket(out var packetSize);

            if (packet != IntPtr.Zero)
            {
                try
                {
                    var data = new byte[packetSize];
                    Marshal.Copy(packet, data, 0, (int)packetSize);
                    _ = ProcessPacketAsync(data, ct);
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

    private async Task ProcessPacketAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            var packet = IPPacket.Parse(data);
            if (packet == null)
                return;

            if (!packet.IsTCP || packet.DestinationPort == null)
                return;

            var destPort = packet.DestinationPort.Value;
            if (destPort != 80 && destPort != 443)
                return;

            var destIP = packet.Header.DestinationAddress.ToString();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {packet.Header.SourceAddress} -> {destIP}:{destPort}");

            await ForwardViaProxyAsync(packet, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 处理失败：{ex.Message}");
        }
    }

    private async Task ForwardViaProxyAsync(IPPacket packet, CancellationToken ct)
    {
        if (packet.DestinationPort == null)
            return;

        var destIP = packet.Header.DestinationAddress.ToString();
        var destPort = packet.DestinationPort.Value;

        using var proxy = _proxyType switch
        {
            ProxyType.Socks5 => (IDisposableProxyClient?)new Socks5Client(_proxyHost, _proxyPort, _username, _password),
            ProxyType.Http => (IDisposableProxyClient?)new HttpProxyClient(_proxyHost, _proxyPort, _username, _password),
            _ => throw new InvalidOperationException("Unsupported proxy type")
        };

        try
        {
            await proxy.ConnectAsync(destIP, destPort, ct);
            var proxyStream = proxy.GetStream();

            if (packet.Payload.Length > 0)
            {
                await proxyStream.WriteAsync(packet.Payload, ct);
                var responseBuffer = new byte[4096];
                var bytesRead = await proxyStream.ReadAsync(responseBuffer, ct);
                if (bytesRead > 0)
                {
                    Console.WriteLine($"  ✓ 收到响应 {bytesRead} bytes");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 转发失败：{ex.Message}");
        }
    }
}

public interface IDisposableProxyClient : IDisposable
{
    Task ConnectAsync(string host, int port, CancellationToken ct);
    NetworkStream GetStream();
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
