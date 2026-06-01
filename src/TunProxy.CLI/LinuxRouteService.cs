using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Serilog;
using TunProxy.Core;
using TunProxy.Core.Connections;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

/// <summary>
/// Linux route service backed by iproute2.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxRouteService : IRouteService
{
    internal const int TunProxyRouteTable = 51821;
    internal const int MainRulePriority = 100;
    internal const int TunRulePriority = 101;
    internal const int ZeroTierRulePriority = 90;
    internal const int DefaultZeroTierUdpPort = 9993;

    private readonly string _tunIp;
    private readonly string _devName;
    private readonly Func<string, string, (int ExitCode, string Output)> _run;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly HashSet<string> _addedBypassRoutes = new(StringComparer.OrdinalIgnoreCase);

    public LinuxRouteService(
        string tunIp = "10.0.0.1",
        string subnetMask = "255.255.255.0",
        string devName = "tun0")
        : this(tunIp, subnetMask, devName, Run, Environment.GetEnvironmentVariable)
    {
    }

    internal LinuxRouteService(
        string tunIp,
        string subnetMask,
        string devName,
        Func<string, string, (int ExitCode, string Output)> run,
        Func<string, string?> getEnvironmentVariable)
    {
        _tunIp = tunIp;
        _devName = devName;
        _run = run;
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    public string? GetOriginalDefaultGateway() =>
        GetOriginalDefaultRoute()?.Gateway;

    public bool AddBypassRoute(string ip, int prefixLength = 32)
    {
        var route = GetOriginalDefaultRoute();
        if (route == null)
        {
            Log.Warning(
                "[ROUTE] Failed to find original default route; skipping bypass route {IP}/{Prefix}.",
                ip,
                prefixLength);
            return false;
        }

        return AddBypassRoute(ip, prefixLength, route);
    }

    public bool RemoveBypassRoute(string ip)
    {
        var (code, _) = _run("ip", $"route del {ip}");
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
        AddActiveManagementBypassRoutes();
        AddZeroTierBypassRules();

        var routeReady = RunRouteCommand(
            "ip",
            $"route replace default dev {_devName} table {TunProxyRouteTable}",
            "[ROUTE] Failed to add TUN policy default route on {Device}.");
        var mainRuleReady = AddRule(
            MainRulePriority,
            $"table main suppress_prefixlength 0");
        var tunRuleReady = AddRule(
            TunRulePriority,
            $"not fwmark 0x{LinuxSocketMark.TunProxyBypassMark:x} table {TunProxyRouteTable}");

        if (routeReady && mainRuleReady && tunRuleReady)
        {
            return true;
        }

        Log.Warning(
            "[ROUTE] Linux policy route setup incomplete. Route={RouteReady}, MainRule={MainRuleReady}, TunRule={TunRuleReady}",
            routeReady,
            mainRuleReady,
            tunRuleReady);
        return false;
    }

    public bool RemoveDefaultRoute()
    {
        var tunRuleRemoved = DeleteRule(TunRulePriority, $"not fwmark 0x{LinuxSocketMark.TunProxyBypassMark:x} table {TunProxyRouteTable}");
        var mainRuleRemoved = DeleteRule(MainRulePriority, "table main suppress_prefixlength 0");
        RemoveZeroTierBypassRules();
        var routeRemoved = RunRouteCommand(
            "ip",
            $"route flush table {TunProxyRouteTable}",
            "[ROUTE] Failed to flush TUN policy route table {Device}.");

        return tunRuleRemoved && mainRuleRemoved && routeRemoved;
    }

    public void ClearAllBypassRoutes()
    {
        foreach (var ip in _addedBypassRoutes.ToList())
        {
            RemoveBypassRoute(ip);
        }

        _addedBypassRoutes.Clear();
    }

    private bool AddBypassRoute(string ip, int prefixLength, LinuxDefaultRoute route)
    {
        var target = route.ToRouteTarget();
        if (string.IsNullOrWhiteSpace(target))
        {
            Log.Warning(
                "[ROUTE] Original default route has no gateway or device; skipping bypass route {IP}/{Prefix}.",
                ip,
                prefixLength);
            return false;
        }

        var (code, output) = _run("ip", $"route replace {ip}/{prefixLength} {target}");
        if (code == 0)
        {
            _addedBypassRoutes.Add(ip);
            return true;
        }

        Log.Warning(
            "[ROUTE] Failed to add bypass route {IP}/{Prefix} {Target}. Output: {Output}",
            ip,
            prefixLength,
            target,
            output.Trim());
        return false;
    }

    private LinuxDefaultRoute? GetOriginalDefaultRoute()
    {
        var (_, output) = _run("ip", "route show default table main");
        return ParseOriginalDefaultRoute(output, _devName, _tunIp);
    }

    private bool AddRule(int priority, string selector) =>
        RunIdempotentCommand(
            "ip",
            $"rule add pref {priority} {selector}",
            "[ROUTE] Failed to add policy rule pref {Priority}: {Selector}.",
            priority,
            selector);

    private bool DeleteRule(int priority, string selector)
    {
        var (code, output) = _run("ip", $"rule del pref {priority} {selector}");
        if (code == 0 ||
            output.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Cannot find", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Log.Debug(
            "[ROUTE] Failed to delete policy rule pref {Priority}: {Selector}. Output: {Output}",
            priority,
            selector,
            output.Trim());
        return false;
    }

    private bool RunRouteCommand(string file, string args, string messageTemplate)
    {
        var (code, output) = _run(file, args);
        if (code == 0)
        {
            return true;
        }

        Log.Warning(messageTemplate + " Output: {Output}", _devName, output.Trim());
        return false;
    }

    private bool RunIdempotentCommand(
        string file,
        string args,
        string messageTemplate,
        int priority,
        string selector)
    {
        var (code, output) = _run(file, args);
        if (code == 0 || output.Contains("File exists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Log.Warning(messageTemplate + " Output: {Output}", priority, selector, output.Trim());
        return false;
    }

    private void AddZeroTierBypassRules()
    {
        foreach (var port in DiscoverZeroTierUdpPorts(_run("ss", "-Hunp").Output))
        {
            AddRule(ZeroTierRulePriority, $"ipproto udp sport {port} table main");
        }
    }

    private void RemoveZeroTierBypassRules()
    {
        DeleteRule(ZeroTierRulePriority, $"ipproto udp sport {DefaultZeroTierUdpPort} table main");
        foreach (var port in DiscoverZeroTierUdpPorts(_run("ss", "-Hunp").Output))
        {
            DeleteRule(ZeroTierRulePriority, $"ipproto udp sport {port} table main");
        }
    }

    private void AddActiveManagementBypassRoutes()
    {
        var candidates = DiscoverActiveManagementClientAddresses(
            _getEnvironmentVariable("SSH_CONNECTION"),
            _getEnvironmentVariable("SSH_CLIENT"),
            _run("ss", "-Htn state established").Output);

        if (candidates.Count == 0)
        {
            return;
        }

        var route = GetOriginalDefaultRoute();
        if (route == null)
        {
            Log.Warning("[ROUTE] Found active management clients but could not find original default route.");
            return;
        }

        foreach (var address in candidates)
        {
            if (AddBypassRoute(address.ToString(), 32, route))
            {
                Log.Information("[ROUTE] Management bypass route ready: {IP}/32", address);
            }
        }
    }

    internal static LinuxDefaultRoute? ParseOriginalDefaultRoute(
        string routeOutput,
        string tunDeviceName,
        string tunIp)
    {
        foreach (var line in routeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 ||
                !parts[0].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? gateway = null;
            string? device = null;
            for (var i = 1; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("via", StringComparison.OrdinalIgnoreCase))
                {
                    gateway = parts[i + 1];
                    i++;
                    continue;
                }

                if (parts[i].Equals("dev", StringComparison.OrdinalIgnoreCase))
                {
                    device = parts[i + 1];
                    i++;
                }
            }

            if (device != null && device.Equals(tunDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (gateway != null && gateway.Equals(tunIp, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(gateway) || !string.IsNullOrWhiteSpace(device))
            {
                return new LinuxDefaultRoute(gateway, device);
            }
        }

        return null;
    }

    internal static IReadOnlyList<IPAddress> DiscoverActiveManagementClientAddresses(
        string? sshConnection,
        string? sshClient,
        string establishedTcpOutput)
    {
        var addresses = new List<IPAddress>();

        AddCandidate(TryParseFirstIPv4Token(sshConnection));
        AddCandidate(TryParseFirstIPv4Token(sshClient));

        foreach (var line in establishedTcpOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var localEndpoint = parts[^2];
            var peerEndpoint = parts[^1];
            if (!TryParseTcpEndpoint(localEndpoint, out _, out var localPort) ||
                !IsManagementPort(localPort) ||
                !TryParseTcpEndpoint(peerEndpoint, out var peerAddress, out _))
            {
                continue;
            }

            AddCandidate(peerAddress);
        }

        return addresses;

        void AddCandidate(IPAddress? address)
        {
            if (address == null ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) ||
                addresses.Contains(address))
            {
                return;
            }

            addresses.Add(address);
        }
    }

    internal static IReadOnlyList<int> DiscoverZeroTierUdpPorts(string udpSocketOutput)
    {
        var ports = new SortedSet<int> { DefaultZeroTierUdpPort };
        foreach (var line in udpSocketOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("zerotier", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (TryParseTcpEndpoint(part, out _, out var port))
                {
                    ports.Add(port);
                    break;
                }
            }
        }

        return ports.ToList();
    }

    private static IPAddress? TryParseFirstIPv4Token(string? value)
    {
        var token = value?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return IPAddress.TryParse(token, out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork
            ? address
            : null;
    }

    private static bool TryParseTcpEndpoint(string value, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var host = value[..separator].Trim('[', ']');
        var portText = value[(separator + 1)..];
        if (!IPAddress.TryParse(host, out var parsedAddress) ||
            parsedAddress.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(portText, out port))
        {
            return false;
        }

        address = parsedAddress;
        return true;
    }

    private static bool IsManagementPort(int port) =>
        port is 22 or TunProxyProduct.ApiPort;

    private static (int ExitCode, string Output) Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Command failed [{File} {Args}]: {Message}", file, args, ex.Message);
            return (1, ex.Message);
        }
    }
}

internal sealed record LinuxDefaultRoute(string? Gateway, string? Device)
{
    public string ToRouteTarget()
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(Gateway))
        {
            parts.Add("via");
            parts.Add(Gateway);
        }

        if (!string.IsNullOrWhiteSpace(Device))
        {
            parts.Add("dev");
            parts.Add(Device);
        }

        return string.Join(' ', parts);
    }
}
