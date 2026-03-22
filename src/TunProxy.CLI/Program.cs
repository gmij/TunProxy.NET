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

        // 创建 TUN 适配器
        Console.WriteLine("创建 TUN 适配器...");
        _adapter = WintunAdapter.CreateAdapter("TunProxy", "Wintun");
        Console.WriteLine("✓ TUN 适配器创建成功");

        using var session = _adapter.StartSession(0x400000); // 4MB ring buffer
        Console.WriteLine("✓ 会话启动成功");

        // 配置 TUN 接口 IP
        ConfigureTunInterface();

        Console.WriteLine("🥔 TunProxy 运行中，按 Ctrl+C 停止...\n");

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

    private void ConfigureTunInterface()
    {
        // 使用 netsh 配置 TUN 接口 IP
        // 注意：实际部署时需要管理员权限
        Console.WriteLine("配置 TUN 接口 IP: 10.0.0.1/24");
        
        // 这里应该调用 Windows API 或 netsh 来配置
        // 简化版本，用户需要手动配置或使用其他工具
        Console.WriteLine("⚠ 请手动配置 TUN 接口 IP 或使用管理员权限运行");
        Console.WriteLine("  netsh interface ip set address \"TunProxy\" static 10.0.0.1 255.255.255.0");
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
                    // 复制数据包
                    var data = new byte[packetSize];
                    Marshal.Copy(packet, data, 0, (int)packetSize);

                    // 处理数据包
                    _ = ProcessPacketAsync(data, ct);
                }
                finally
                {
                    session.ReleaseReceivePacket(packet);
                }
            }
            else if (Marshal.GetLastWin32Error() == WintunNative.ERROR_NO_MORE_ITEMS)
            {
                // 没有数据包，等待
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

            // 只处理 TCP 流量（HTTP/HTTPS）
            if (!packet.IsTCP || packet.DestinationPort == null)
                return;

            var destPort = packet.DestinationPort.Value;

            // 只代理 HTTP (80) 和 HTTPS (443) 流量
            if (destPort != 80 && destPort != 443)
                return;

            var destIP = packet.Header.DestinationIPAddress;
            var sourceIP = packet.Header.SourceIPAddress;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {sourceIP} -> {destIP}:{destPort} ({packet.Payload.Length} bytes)");

            // 通过代理转发
            await ForwardViaProxyAsync(packet, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ 处理数据包失败：{ex.Message}");
        }
    }

    private async Task ForwardViaProxyAsync(IPPacket packet, CancellationToken ct)
    {
        if (packet.DestinationPort == null)
            return;

        var destIP = packet.Header.DestinationIPAddress.ToString();
        var destPort = packet.DestinationPort.Value;

        // 创建代理连接
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

            // 发送 payload 到代理
            if (packet.Payload.Length > 0)
            {
                await proxyStream.WriteAsync(packet.Payload, ct);

                // 读取响应（简化版本，实际应该保持长连接）
                var responseBuffer = new byte[4096];
                var bytesRead = await proxyStream.ReadAsync(responseBuffer, ct);

                if (bytesRead > 0)
                {
                    // TODO: 将响应写回 TUN 设备
                    Console.WriteLine($"  ✓ 收到响应 {bytesRead} bytes");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 代理转发失败：{ex.Message}");
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

        // 解析命令行参数
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
🥔 TunProxy - .NET 8 TUN 代理

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
