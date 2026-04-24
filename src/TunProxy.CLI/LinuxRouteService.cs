using System.Diagnostics;
using System.Runtime.Versioning;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

/// <summary>
/// Linux 路由服务（ip route 命令）
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxRouteService : IRouteService
{
    private readonly string _devName;
    private readonly HashSet<string> _addedBypassRoutes = new(StringComparer.OrdinalIgnoreCase);

    public LinuxRouteService(string tunIp = "10.0.0.1", string subnetMask = "255.255.255.0",
        string devName = "tun0")
    {
        _devName = devName;
    }

    public string? GetOriginalDefaultGateway()
    {
        var (_, output) = Run("ip", "route show default");
        var match = System.Text.RegularExpressions.Regex.Match(output, @"default via (\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    public bool AddBypassRoute(string ip, int prefixLength = 32)
    {
        var gw = GetOriginalDefaultGateway();
        if (gw == null)
        {
            Log.Warning("[ROUTE] 无法获取原始默认网关，跳过：{IP}", ip);
            return false;
        }
        var (code, _) = Run("ip", $"route add {ip}/{prefixLength} via {gw}");
        if (code == 0) _addedBypassRoutes.Add(ip);
        return code == 0;
    }

    public bool RemoveBypassRoute(string ip)
    {
        var (code, _) = Run("ip", $"route del {ip}");
        return code == 0;
    }

    public bool RemoveTrackedBypassRoute(string ip)
    {
        if (!_addedBypassRoutes.Remove(ip))
        {
            return false;
        }

        if (RemoveBypassRoute(ip))
        {
            return true;
        }

        _addedBypassRoutes.Add(ip);
        return false;
    }

    public bool AddDefaultRoute()
    {
        var (code, _) = Run("ip", $"route add default dev {_devName} metric 1");
        return code == 0;
    }

    public bool RemoveDefaultRoute()
    {
        var (code, _) = Run("ip", $"route del default dev {_devName}");
        return code == 0;
    }

    public void ClearAllBypassRoutes()
    {
        foreach (var ip in _addedBypassRoutes.ToList())
            RemoveBypassRoute(ip);
        _addedBypassRoutes.Clear();
    }

    private static (int ExitCode, string Output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] 命令失败 [{File} {Args}]：{Msg}", file, args, ex.Message);
            return (1, ex.Message);
        }
    }
}
