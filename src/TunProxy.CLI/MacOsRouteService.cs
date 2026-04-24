using System.Diagnostics;
using System.Runtime.Versioning;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

/// <summary>
/// macOS 路由服务（route / netstat 命令）
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsRouteService : IRouteService
{
    private readonly string _tunIp;
    private readonly HashSet<string> _addedBypassRoutes = new(StringComparer.OrdinalIgnoreCase);

    public MacOsRouteService(string tunIp = "10.0.0.1", string subnetMask = "255.255.255.0")
    {
        _tunIp = tunIp;
    }

    public string? GetOriginalDefaultGateway()
    {
        var (_, output) = Run("netstat", "-rn");
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "default" && parts[1] != _tunIp)
                return parts[1];
        }
        return null;
    }

    public bool AddBypassRoute(string ip, int prefixLength = 32)
    {
        var gw = GetOriginalDefaultGateway();
        if (gw == null)
        {
            Log.Warning("[ROUTE] 无法获取原始默认网关，跳过：{IP}", ip);
            return false;
        }
        var (code, _) = Run("route", $"add -{(prefixLength == 32 ? "host" : "net")} {ip} {gw}");
        if (code == 0) _addedBypassRoutes.Add(ip);
        return code == 0;
    }

    public bool RemoveBypassRoute(string ip)
    {
        var (code, _) = Run("route", $"delete {ip}");
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
        // 默认路由由 MacOsTunDevice.Configure 中的 `route add default -interface utunX` 设置
        return true;
    }

    public bool RemoveDefaultRoute()
    {
        var (code, _) = Run("route", "delete default");
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
