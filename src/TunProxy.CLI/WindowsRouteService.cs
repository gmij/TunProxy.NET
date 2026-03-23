using System.Diagnostics;
using System.Net.NetworkInformation;
using Serilog;

namespace TunProxy.CLI;

/// <summary>
/// Windows 路由服务
/// </summary>
public class WindowsRouteService
{
    private readonly string _tunIpAddress;
    private readonly string _tunSubnetMask;

    public WindowsRouteService(string tunIpAddress = "10.0.0.1", string tunSubnetMask = "255.255.255.0")
    {
        _tunIpAddress = tunIpAddress;
        _tunSubnetMask = tunSubnetMask;
    }

    /// <summary>
    /// 获取 Wintun 适配器的实际网络连接名称
    /// </summary>
    private string GetTunInterfaceName()
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                    ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    ni.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase));
            return adapter?.Name ?? "TunProxy";
        }
        catch { return "TunProxy"; }
    }

    /// <summary>
    /// 获取 TUN 接口索引
    /// </summary>
    public uint? GetTunInterfaceIndex()
    {
        try
        {
            var tunInterfaceName = GetTunInterfaceName();
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name.Equals(tunInterfaceName, StringComparison.OrdinalIgnoreCase));
            if (adapter == null) return null;
            var index = adapter.GetIPProperties().GetIPv4Properties()?.Index;
            return index.HasValue ? (uint?)index.Value : null;
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] 获取 TUN 接口索引失败：{Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 添加默认路由（全局流量走 TUN）
    /// </summary>
    public bool AddDefaultRoute()
    {
        var tunInterfaceName = GetTunInterfaceName();
        var routes = GetRouteTable();

        // 清理指向 TUN IP 但接口不对的过期路由
        foreach (var stale in routes.Where(r => r.Network == "0.0.0.0" && r.Gateway == _tunIpAddress && r.Interface != _tunIpAddress))
        {
            Log.Debug("[ROUTE] 清理过期路由：0.0.0.0 -> {Gateway} via {Interface}", stale.Gateway, stale.Interface);
            ExecuteNetshCommand($"interface ip delete route 0.0.0.0/0 {stale.Gateway}");
        }

        // 已存在则跳过
        if (routes.Any(r => r.Network == "0.0.0.0" && r.Gateway == _tunIpAddress && r.Interface == _tunIpAddress))
        {
            Log.Debug("[ROUTE] TUN 默认路由已存在，跳过");
            return true;
        }

        Log.Debug("[ROUTE] 添加 TUN 默认路由：0.0.0.0/0 via {Interface}", tunInterfaceName);
        return ExecuteNetshCommand($"interface ip add route 0.0.0.0/0 \"{tunInterfaceName}\" {_tunIpAddress} metric=1");
    }

    /// <summary>
    /// 获取原始默认网关（非 TUN 网关）
    /// </summary>
    public string? GetOriginalDefaultGateway()
    {
        var routes = GetRouteTable();
        return routes
            .Where(r => r.Network == "0.0.0.0" && r.Gateway != _tunIpAddress)
            .OrderBy(r => int.TryParse(r.Metric, out var m) ? m : 9999)
            .FirstOrDefault()?.Gateway;
    }

    /// <summary>
    /// 为指定 IP/子网添加绕过路由（走原始网关，不经过 TUN）
    /// prefixLength=24 时传入子网地址（如 106.11.43.0），添加 /24 路由覆盖整段
    /// </summary>
    public bool AddBypassRoute(string ipAddress, int prefixLength = 32)
    {
        var gateway = GetOriginalDefaultGateway();
        if (string.IsNullOrEmpty(gateway))
        {
            Log.Warning("[ROUTE] 无法获取原始默认网关，跳过绕过路由：{IP}/{Prefix}", ipAddress, prefixLength);
            return false;
        }

        var mask = prefixLength switch
        {
            24 => "255.255.255.0",
            16 => "255.255.0.0",
            _  => "255.255.255.255"  // /32
        };

        var (exitCode, output) = ExecuteCommandWithOutput("route", $"add {ipAddress} mask {mask} {gateway}");
        if (exitCode == 0)
        {
            Log.Debug("[ROUTE] 绕过路由已添加：{IP}/{Prefix} via {Gateway}", ipAddress, prefixLength, gateway);
            return true;
        }

        // 路由已存在也算成功
        if (output.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("已存在", StringComparison.OrdinalIgnoreCase) ||
            RouteExists(ipAddress, mask))
        {
            Log.Debug("[ROUTE] 绕过路由已存在（复用）：{IP}/{Prefix}", ipAddress, prefixLength);
            return true;
        }

        Log.Warning("[ROUTE] 添加绕过路由失败：{IP}/{Prefix} via {Gateway}，输出：{Output}", ipAddress, prefixLength, gateway, output.Trim());
        return false;
    }

    /// <summary>
    /// 删除绕过路由
    /// </summary>
    public bool RemoveBypassRoute(string ipAddress)
    {
        var (exitCode, _) = ExecuteCommandWithOutput("route", $"delete {ipAddress}");
        return exitCode == 0;
    }

    /// <summary>
    /// 检查指定 IP 的 /32 路由是否已存在
    /// </summary>
    private bool RouteExists(string ipAddress, string mask = "255.255.255.255")
    {
        var routes = GetRouteTable();
        return routes.Any(r => r.Network == ipAddress && r.Netmask == mask);
    }

    /// <summary>
    /// 删除默认路由
    /// </summary>
    public bool RemoveDefaultRoute()
    {
        var tunInterfaceName = GetTunInterfaceName();
        return ExecuteNetshCommand($"interface ip delete route 0.0.0.0/0 \"{tunInterfaceName}\"");
    }

    /// <summary>
    /// 添加指定网段路由
    /// </summary>
    public bool AddRoute(string network, string mask, string? gateway = null)
    {
        var gw = gateway ?? _tunIpAddress;
        var tunInterfaceName = GetTunInterfaceName();
        return ExecuteNetshCommand($"interface ip add route {network}/{mask} \"{tunInterfaceName}\" {gw}");
    }

    /// <summary>
    /// 获取当前路由表
    /// </summary>
    public List<RouteEntry> GetRouteTable()
    {
        var routes = new List<RouteEntry>();
        try
        {
            var (_, output) = ExecuteCommandWithOutput("route", "PRINT");
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Network") || trimmed.StartsWith("=") || string.IsNullOrWhiteSpace(trimmed))
                    continue;
                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                    routes.Add(new RouteEntry { Network = parts[0], Netmask = parts[1], Gateway = parts[2], Interface = parts[3], Metric = parts[4] });
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] 获取路由表失败：{Message}", ex.Message);
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
            var interfaceIndex = GetTunInterfaceIndex();
            result.TunInterfaceExists = interfaceIndex.HasValue;
            result.TunInterfaceIndex = interfaceIndex;
            if (!result.TunInterfaceExists) { result.Issues.Add("TUN 接口不存在"); return result; }

            var routes = GetRouteTable();
            var defaultRoute = routes.FirstOrDefault(r => r.Network == "0.0.0.0" && r.Gateway == _tunIpAddress && r.Interface == _tunIpAddress);
            result.HasDefaultRoute = defaultRoute != null;
            result.DefaultRouteMetric = defaultRoute?.Metric;
            if (!result.HasDefaultRoute) result.Issues.Add("默认路由不存在");

            var competingRoutes = routes.Where(r => r.Network == "0.0.0.0" && r.Gateway != _tunIpAddress).ToList();
            result.CompetingRoutes = competingRoutes.Count;
            if (defaultRoute != null && competingRoutes.Any())
            {
                var tunMetric = int.TryParse(defaultRoute.Metric, out var tm) ? tm : 999;
                if (competingRoutes.Any(r => int.TryParse(r.Metric, out var m) && m < tunMetric))
                    result.Issues.Add($"存在优先级更高的其他默认路由 (TUN metric={tunMetric})");
            }
            result.InternetAccessible = TestInternetConnectivity();
            result.TunIpAddress = GetTunInterfaceIpAddress();
        }
        catch (Exception ex) { result.Issues.Add($"诊断过程出错：{ex.Message}"); }
        return result;
    }

    private bool TestInternetConnectivity()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return client.GetAsync("http://www.baidu.com").Result.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private string? GetTunInterfaceIpAddress()
    {
        try
        {
            var tunInterfaceName = GetTunInterfaceName();
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name.Equals(tunInterfaceName, StringComparison.OrdinalIgnoreCase));
            return adapter?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// 执行 netsh 命令（不需要捕获输出）
    /// </summary>
    private bool ExecuteNetshCommand(string command)
    {
        var (exitCode, _) = ExecuteCommandWithOutput("netsh", command);
        return exitCode == 0;
    }

    /// <summary>
    /// 执行命令并返回退出码和输出
    /// </summary>
    private static (int ExitCode, string Output) ExecuteCommandWithOutput(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] 执行命令失败 [{FileName} {Args}]：{Message}", fileName, arguments, ex.Message);
            return (1, ex.Message);
        }
    }
}

public class RouteEntry
{
    public string Network { get; set; } = "";
    public string Netmask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Interface { get; set; } = "";
    public string Metric { get; set; } = "";
}

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
        Log.Information("=== 路由诊断报告 ===");
        Log.Information("TUN 接口：{Status}（索引 {Index}，IP {IP}）",
            TunInterfaceExists ? "存在" : "不存在", TunInterfaceIndex, TunIpAddress);
        Log.Information("默认路由：{Status}（Metric={Metric}，竞争路由 {Competing} 条）",
            HasDefaultRoute ? "存在" : "缺失", DefaultRouteMetric, CompetingRoutes);
        Log.Information("互联网连通：{Status}", InternetAccessible ? "是" : "否");
        foreach (var issue in Issues)
            Log.Warning("路由问题：{Issue}", issue);
        if (Issues.Count == 0)
            Log.Information("路由诊断：所有检查通过");
    }
}
