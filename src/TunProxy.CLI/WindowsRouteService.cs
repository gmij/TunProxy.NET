using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TunProxy.CLI;

/// <summary>
/// Windows 路由服务
/// </summary>
public class WindowsRouteService
{
    private readonly string _tunInterfaceName;
    private readonly string _tunIpAddress;
    private readonly string _tunSubnetMask;

    public WindowsRouteService(string tunInterfaceName = "TunProxy", string tunIpAddress = "10.0.0.1", string tunSubnetMask = "255.255.255.0")
    {
        _tunInterfaceName = tunInterfaceName;
        _tunIpAddress = tunIpAddress;
        _tunSubnetMask = tunSubnetMask;
    }

    /// <summary>
    /// 获取 TUN 接口索引
    /// </summary>
    public uint? GetTunInterfaceIndex()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name.Equals(_tunInterfaceName, StringComparison.OrdinalIgnoreCase));
            
            if (adapter == null)
                return null;

            var properties = adapter.GetIPProperties();
            var index = properties.GetIPv4Properties()?.Index;
            return index.HasValue ? (uint?)index.Value : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ROUTE] 获取 TUN 接口索引失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 添加默认路由
    /// </summary>
    public bool AddDefaultRoute()
    {
        // 先检查是否已存在 TUN 默认路由
        var routes = GetRouteTable();
        var existingTunRoute = routes.FirstOrDefault(r =>
            r.Network == "0.0.0.0" && r.Gateway == _tunIpAddress);

        if (existingTunRoute != null)
        {
            Console.WriteLine($"[ROUTE] TUN 默认路由已存在，跳过添加");
            return true;
        }

        // 添加路由，使用较低的 metric 确保优先级
        return ExecuteNetshCommand($"interface ip add route 0.0.0.0/0 \"{_tunInterfaceName}\" {_tunIpAddress} metric=1");
    }

    /// <summary>
    /// 删除默认路由
    /// </summary>
    public bool RemoveDefaultRoute()
    {
        return ExecuteNetshCommand($"interface ip delete route 0.0.0.0/0 \"{_tunInterfaceName}\"");
    }

    /// <summary>
    /// 添加指定网段路由
    /// </summary>
    public bool AddRoute(string network, string mask, string? gateway = null)
    {
        var gw = gateway ?? _tunIpAddress;
        return ExecuteNetshCommand($"interface ip add route {network}/{mask} \"{_tunInterfaceName}\" {gw}");
    }

    /// <summary>
    /// 删除指定路由
    /// </summary>
    public bool RemoveRoute(string network, string mask)
    {
        return ExecuteNetshCommand($"interface ip delete route {network}/{mask} \"{_tunInterfaceName}\"");
    }

    /// <summary>
    /// 获取当前路由表
    /// </summary>
    public List<RouteEntry> GetRouteTable()
    {
        var routes = new List<RouteEntry>();
        
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "route",
                Arguments = "PRINT",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            proc?.WaitForExit();

            // 解析 route print 输出
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Network") || trimmed.StartsWith("=") || string.IsNullOrWhiteSpace(trimmed))
                    continue;

                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    routes.Add(new RouteEntry
                    {
                        Network = parts[0],
                        Netmask = parts[1],
                        Gateway = parts[2],
                        Interface = parts[3],
                        Metric = parts[4]
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ROUTE] 获取路由表失败：{ex.Message}");
        }

        return routes;
    }

    /// <summary>
    /// 诊断路由配置
    /// </summary>
    public RouteDiagnosisResult Diagnose()
    {
        var result = new RouteDiagnosisResult();

        try
        {
            // 1. 检查 TUN 接口是否存在
            var interfaceIndex = GetTunInterfaceIndex();
            result.TunInterfaceExists = interfaceIndex.HasValue;
            result.TunInterfaceIndex = interfaceIndex;

            if (!result.TunInterfaceExists)
            {
                result.Issues.Add("TUN 接口不存在");
                return result;
            }

            // 2. 检查默认路由
            var routes = GetRouteTable();
            var defaultRoute = routes.FirstOrDefault(r =>
                r.Network == "0.0.0.0" && r.Gateway == _tunIpAddress);

            result.HasDefaultRoute = defaultRoute != null;
            result.DefaultRouteMetric = defaultRoute?.Metric;

            if (!result.HasDefaultRoute)
            {
                result.Issues.Add("默认路由不存在");
            }

            // 3. 检查路由优先级（metric 越小优先级越高）
            var competingRoutes = routes.Where(r =>
                r.Network == "0.0.0.0" && r.Gateway != _tunIpAddress).ToList();

            result.CompetingRoutes = competingRoutes.Count;

            if (defaultRoute != null && competingRoutes.Any())
            {
                var tunMetric = int.TryParse(defaultRoute.Metric, out var tm) ? tm : 999;
                var hasHigherPriority = competingRoutes.Any(r =>
                {
                    var metric = int.TryParse(r.Metric, out var m) ? m : 999;
                    return metric < tunMetric;
                });

                if (hasHigherPriority)
                {
                    result.Issues.Add($"存在优先级更高的其他默认路由 (TUN metric={tunMetric})");
                    foreach (var route in competingRoutes.Where(r => int.Parse(r.Metric) < tunMetric))
                    {
                        result.Issues.Add($"  - Gateway={route.Gateway}, Metric={route.Metric}");
                    }
                }
            }

            // 4. 测试网络连通性
            result.InternetAccessible = TestInternetConnectivity();

            // 5. 获取 TUN 接口 IP
            result.TunIpAddress = GetTunInterfaceIpAddress();
        }
        catch (Exception ex)
        {
            result.Issues.Add($"诊断过程出错：{ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 测试互联网连通性
    /// </summary>
    private bool TestInternetConnectivity()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = client.GetAsync("http://www.baidu.com").Result;
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取 TUN 接口 IP 地址
    /// </summary>
    private string? GetTunInterfaceIpAddress()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name.Equals(_tunInterfaceName, StringComparison.OrdinalIgnoreCase));
            
            if (adapter == null)
                return null;

            var properties = adapter.GetIPProperties();
            var unicast = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            
            return unicast?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 执行 netsh 命令
    /// </summary>
    private bool ExecuteNetshCommand(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            
            return proc?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ROUTE] 执行命令失败：{ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// 路由条目
/// </summary>
public class RouteEntry
{
    public string Network { get; set; } = "";
    public string Netmask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Interface { get; set; } = "";
    public string Metric { get; set; } = "";
}

/// <summary>
/// 路由诊断结果
/// </summary>
public class RouteDiagnosisResult
{
    public bool TunInterfaceExists { get; set; }
    public uint? TunInterfaceIndex { get; set; }
    public bool HasDefaultRoute { get; set; }
    public string? DefaultRouteMetric { get; set; }
    public int CompetingRoutes { get; set; }
    public bool InternetAccessible { get; set; }
    public string? TunIpAddress { get; set; }
    public List<string> Issues { get; set; } = new();

    public void Print()
    {
        Console.WriteLine("");
        Console.WriteLine("=== 路由诊断报告 ===");
        Console.WriteLine("");
        Console.WriteLine($"TUN 接口存在：{(TunInterfaceExists ? "✓ 是" : "✗ 否")}");
        if (TunInterfaceIndex.HasValue)
            Console.WriteLine($"TUN 接口索引：{TunInterfaceIndex}");
        if (!string.IsNullOrEmpty(TunIpAddress))
            Console.WriteLine($"TUN 接口 IP: {TunIpAddress}");
        Console.WriteLine($"默认路由存在：{(HasDefaultRoute ? "✓ 是" : "✗ 否")}");
        if (!string.IsNullOrEmpty(DefaultRouteMetric))
            Console.WriteLine($"默认路由 Metric: {DefaultRouteMetric}");
        Console.WriteLine($"竞争路由数量：{CompetingRoutes}");
        Console.WriteLine($"互联网连通：{(InternetAccessible ? "✓ 是" : "✗ 否")}");
        
        if (Issues.Count > 0)
        {
            Console.WriteLine("");
            Console.WriteLine("发现的问题：");
            foreach (var issue in Issues)
            {
                Console.WriteLine($"  ✗ {issue}");
            }
        }
        else
        {
            Console.WriteLine("");
            Console.WriteLine("✓ 所有检查通过");
        }
        Console.WriteLine("");
    }
}
