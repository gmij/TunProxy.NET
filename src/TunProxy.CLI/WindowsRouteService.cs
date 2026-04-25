using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

/// <summary>
/// Windows route management backed by route.exe and netsh.exe.
/// </summary>
public class WindowsRouteService : IRouteService
{
    private const string PreferredTunInterfaceName = "TunProxy";
    private readonly string _tunIpAddress;
    private readonly string _tunSubnetMask;
    private readonly HashSet<string> _addedBypassRoutes = new(StringComparer.OrdinalIgnoreCase);
    private string? _cachedOriginalDefaultGateway;

    public WindowsRouteService(string tunIpAddress = "10.0.0.1", string tunSubnetMask = "255.255.255.0")
    {
        _tunIpAddress = tunIpAddress;
        _tunSubnetMask = tunSubnetMask;
    }

    private string GetTunInterfaceName()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Select(CreateTunInterfaceCandidate)
                .ToList();
            return ResolveTunInterfaceName(candidates, _tunIpAddress);
        }
        catch
        {
            return PreferredTunInterfaceName;
        }
    }

    public uint? GetTunInterfaceIndex()
    {
        try
        {
            var tunInterfaceName = GetTunInterfaceName();
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name.Equals(tunInterfaceName, StringComparison.OrdinalIgnoreCase));

            var index = adapter?.GetIPProperties().GetIPv4Properties()?.Index;
            return index.HasValue ? (uint)index.Value : null;
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to get TUN interface index: {Message}", ex.Message);
            return null;
        }
    }

    public bool AddDefaultRoute()
    {
        var tunInterfaceName = GetTunInterfaceName();
        var routes = GetRouteTable();

        foreach (var stale in routes.Where(route =>
                     route.Network == "0.0.0.0" &&
                     route.Gateway == _tunIpAddress &&
                     !IsTunDefaultRoute(route, _tunIpAddress)))
        {
            Log.Debug(
                "[ROUTE] Removing stale TUN default route 0.0.0.0 via {Gateway} on {Interface}",
                stale.Gateway,
                stale.Interface);
            ExecuteNetshCommand($"interface ipv4 delete route 0.0.0.0/0 \"{tunInterfaceName}\" {_tunIpAddress}");
        }

        if (HasTunDefaultRoute())
        {
            Log.Information("[ROUTE] TUN default route already exists.");
            return true;
        }

        Log.Information("[ROUTE] Adding TUN default route 0.0.0.0/0 via interface {Interface}.", tunInterfaceName);
        if (TryAddDefaultRoute(tunInterfaceName, _tunIpAddress))
        {
            return true;
        }

        Log.Warning(
            "[ROUTE] Retrying TUN default route as an on-link route. Interface={Interface}, TUN={TunIp}",
            tunInterfaceName,
            _tunIpAddress);
        return TryAddDefaultRoute(tunInterfaceName, "0.0.0.0");
    }

    public string? GetOriginalDefaultGateway()
    {
        if (!string.IsNullOrWhiteSpace(_cachedOriginalDefaultGateway))
        {
            return _cachedOriginalDefaultGateway;
        }

        var routeGateway = GetRouteTable()
            .Where(route =>
                route.Network == "0.0.0.0" &&
                route.Gateway != _tunIpAddress &&
                !IsOnLinkGateway(route.Gateway) &&
                IsIPv4Address(route.Gateway))
            .OrderBy(route => int.TryParse(route.Metric, out var metric) ? metric : int.MaxValue)
            .FirstOrDefault()
            ?.Gateway;

        if (!string.IsNullOrWhiteSpace(routeGateway))
        {
            Log.Information("[ROUTE] Original gateway selected from route table: {Gateway}", routeGateway);
            _cachedOriginalDefaultGateway = routeGateway;
        }

        if (!string.IsNullOrWhiteSpace(routeGateway))
        {
            return routeGateway;
        }

        var nicGateway = GetGatewayFromNetworkInterfaces();
        if (!string.IsNullOrWhiteSpace(nicGateway))
        {
            _cachedOriginalDefaultGateway = nicGateway;
            return nicGateway;
        }

        return null;
    }

    public IPAddress? GetLocalAddressForDestination(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var address = FindLocalAddressForDestination(GetRouteTable(), destination, _tunIpAddress);
        if (address != null)
        {
            Log.Information(
                "[ROUTE] Local address for destination {Destination} selected from route table: {LocalAddress}",
                destination,
                address);
        }

        return address;
    }

    public bool AddBypassRoute(string ipAddress, int prefixLength = 32)
    {
        if (prefixLength == 32 && TryFindExistingSpecificRoute(ipAddress, out var existingRoute))
        {
            Log.Information(
                "[ROUTE] Bypass route already covered by existing route: {IP}/{Prefix} via {Gateway} on {Interface}",
                ipAddress,
                prefixLength,
                existingRoute.Gateway,
                existingRoute.Interface);
            return true;
        }

        var gateway = GetOriginalDefaultGateway();
        if (string.IsNullOrWhiteSpace(gateway))
        {
            Log.Warning(
                "[ROUTE] Failed to find original default gateway; skipping bypass route {IP}/{Prefix}.",
                ipAddress,
                prefixLength);
            return false;
        }

        var mask = prefixLength switch
        {
            24 => "255.255.255.0",
            16 => "255.255.0.0",
            _ => "255.255.255.255"
        };

        var (exitCode, output) = ExecuteCommandWithOutput("route", $"add {ipAddress} mask {mask} {gateway}");
        if (exitCode == 0)
        {
            Log.Information("[ROUTE] Bypass route ready: {IP}/{Prefix} via {Gateway}", ipAddress, prefixLength, gateway);
            _addedBypassRoutes.Add(ipAddress);
            return true;
        }

        if (IsAlreadyExistsOutput(output) || RouteExists(ipAddress, mask))
        {
            Log.Information("[ROUTE] Bypass route already exists: {IP}/{Prefix}", ipAddress, prefixLength);
            _addedBypassRoutes.Add(ipAddress);
            return true;
        }

        Log.Warning(
            "[ROUTE] Failed to add bypass route {IP}/{Prefix} via {Gateway}. Output: {Output}",
            ipAddress,
            prefixLength,
            gateway,
            output.Trim());
        return false;
    }

    internal bool TryFindExistingSpecificRoute(string ipAddress, out RouteEntry route)
    {
        route = GetRouteTable()
            .Where(candidate => IsSpecificRouteForDestination(candidate, ipAddress, _tunIpAddress))
            .OrderByDescending(candidate => GetPrefixLength(candidate.Netmask))
            .ThenBy(candidate => int.TryParse(candidate.Metric, out var metric) ? metric : int.MaxValue)
            .FirstOrDefault() ?? new RouteEntry();

        return !string.IsNullOrWhiteSpace(route.Network);
    }

    public bool RemoveBypassRoute(string ipAddress)
    {
        var (exitCode, output) = ExecuteCommandWithOutput("route", $"delete {ipAddress}");
        if (exitCode != 0)
        {
            Log.Debug("[ROUTE] Failed to remove bypass route {IP}. Output: {Output}", ipAddress, output.Trim());
        }

        return exitCode == 0;
    }

    public bool RemoveTrackedBypassRoute(string ipAddress)
    {
        if (!_addedBypassRoutes.Remove(ipAddress))
        {
            return false;
        }

        if (RemoveBypassRoute(ipAddress))
        {
            return true;
        }

        _addedBypassRoutes.Add(ipAddress);
        return false;
    }

    public bool RemoveDefaultRoute()
    {
        var tunInterfaceName = GetTunInterfaceName();
        var removed = ExecuteNetshCommand($"interface ipv4 delete route 0.0.0.0/0 \"{tunInterfaceName}\" {_tunIpAddress}");
        var removedOnLink = ExecuteNetshCommand($"interface ipv4 delete route 0.0.0.0/0 \"{tunInterfaceName}\" 0.0.0.0");
        return removed || removedOnLink;
    }

    public void ClearAllBypassRoutes()
    {
        if (_addedBypassRoutes.Count == 0)
        {
            return;
        }

        Log.Information("[ROUTE] Removing {Count} bypass route(s).", _addedBypassRoutes.Count);
        foreach (var ip in _addedBypassRoutes.ToList())
        {
            RemoveBypassRoute(ip);
            Log.Debug("[ROUTE] Removed bypass route: {IP}", ip);
        }

        _addedBypassRoutes.Clear();
    }

    public bool AddRoute(string network, string mask, string? gateway = null)
    {
        var gw = gateway ?? _tunIpAddress;
        var tunInterfaceName = GetTunInterfaceName();
        return ExecuteNetshCommand($"interface ipv4 add route {network}/{mask} \"{tunInterfaceName}\" {gw}");
    }

    public List<RouteEntry> GetRouteTable()
    {
        var routes = new List<RouteEntry>();
        try
        {
            var (_, output) = ExecuteCommandWithOutput("route", "PRINT");
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) ||
                    trimmed.StartsWith("Network", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("=", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !IsIPv4Address(parts[0]) || !IsIPv4Address(parts[1]))
                {
                    continue;
                }

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
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to read route table: {Message}", ex.Message);
        }

        return routes;
    }

    public RouteEntry? GetTunDefaultRoute() =>
        GetRouteTable().FirstOrDefault(route => IsTunDefaultRoute(route, _tunIpAddress));

    public bool HasTunDefaultRoute() => GetTunDefaultRoute() != null;

    public RouteDiagnosisResult Diagnose()
    {
        var result = new RouteDiagnosisResult();
        try
        {
            var interfaceIndex = GetTunInterfaceIndex();
            result.TunInterfaceExists = interfaceIndex.HasValue;
            result.TunInterfaceIndex = interfaceIndex;
            if (!result.TunInterfaceExists)
            {
                result.Issues.Add("TUN interface does not exist.");
                return result;
            }

            var routes = GetRouteTable();
            var defaultRoute = routes.FirstOrDefault(route => IsTunDefaultRoute(route, _tunIpAddress));
            result.HasDefaultRoute = defaultRoute != null;
            result.DefaultRouteMetric = defaultRoute?.Metric;
            if (!result.HasDefaultRoute)
            {
                result.Issues.Add("TUN default route does not exist.");
            }

            var competingRoutes = routes
                .Where(route => route.Network == "0.0.0.0" && !IsTunDefaultRoute(route, _tunIpAddress))
                .ToList();
            result.CompetingRoutes = competingRoutes.Count;

            if (defaultRoute != null && int.TryParse(defaultRoute.Metric, out var tunMetric))
            {
                if (competingRoutes.Any(route => int.TryParse(route.Metric, out var metric) && metric < tunMetric))
                {
                    result.Issues.Add($"Another default route has higher priority than TUN metric {tunMetric}.");
                }
            }

            result.InternetAccessible = TestInternetConnectivity();
            result.TunIpAddress = GetTunInterfaceIpAddress();
        }
        catch (Exception ex)
        {
            result.Issues.Add($"Route diagnosis failed: {ex.Message}");
        }

        return result;
    }

    internal static bool IsOnLinkGateway(string gateway)
    {
        return gateway.Equals("On-link", StringComparison.OrdinalIgnoreCase) ||
               gateway.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
               gateway.Contains("link", StringComparison.OrdinalIgnoreCase) ||
               gateway.Contains("链路", StringComparison.OrdinalIgnoreCase) ||
               gateway.Contains("鏈路", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTunDefaultRoute(RouteEntry route, string tunIpAddress)
    {
        if (route.Network != "0.0.0.0")
        {
            return false;
        }

        if (route.Gateway.Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return route.Interface.Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase) &&
               IsOnLinkGateway(route.Gateway);
    }

    private bool TryAddDefaultRoute(string tunInterfaceName, string nextHop)
    {
        var command = $"interface ipv4 add route 0.0.0.0/0 \"{tunInterfaceName}\" {nextHop} metric=1";
        var (exitCode, output) = ExecuteCommandWithOutput("netsh", command);
        if (exitCode == 0 || IsAlreadyExistsOutput(output))
        {
            if (HasTunDefaultRoute())
            {
                Log.Information("[ROUTE] TUN default route ready. NextHop={NextHop}", nextHop);
                return true;
            }

            Log.Warning(
                "[ROUTE] netsh accepted the default route command, but route PRINT does not show TUN default route yet. NextHop={NextHop}, Output={Output}",
                nextHop,
                output.Trim());
            return false;
        }

        Log.Warning(
            "[ROUTE] Failed to add TUN default route. Command=netsh {Command}, ExitCode={ExitCode}, Output={Output}",
            command,
            exitCode,
            output.Trim());
        return false;
    }

    private string? GetGatewayFromNetworkInterfaces()
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsUsablePhysicalInterface(networkInterface))
                {
                    continue;
                }

                var gateway = networkInterface.GetIPProperties().GatewayAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.Any.Equals(address) &&
                        !IPAddress.None.Equals(address));

                if (gateway == null)
                {
                    continue;
                }

                var gatewayText = gateway.ToString();
                if (gatewayText == _tunIpAddress)
                {
                    continue;
                }

                Log.Information(
                    "[ROUTE] Original gateway selected from interface {Interface}: {Gateway}",
                    networkInterface.Name,
                    gatewayText);
                return gatewayText;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to read gateway from network interfaces: {Message}", ex.Message);
        }

        return null;
    }

    private bool RouteExists(string ipAddress, string mask = "255.255.255.255")
    {
        return GetRouteTable().Any(route => route.Network == ipAddress && route.Netmask == mask);
    }

    private bool TestInternetConnectivity()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            return client.GetAsync("http://www.baidu.com").Result.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string? GetTunInterfaceIpAddress()
    {
        try
        {
            var tunInterfaceName = GetTunInterfaceName();
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.Name.Equals(tunInterfaceName, StringComparison.OrdinalIgnoreCase));

            return adapter?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address
                .ToString();
        }
        catch
        {
            return null;
        }
    }

    private bool ExecuteNetshCommand(string command)
    {
        var (exitCode, output) = ExecuteCommandWithOutput("netsh", command);
        if (exitCode != 0)
        {
            Log.Debug("[ROUTE] netsh command failed. Command={Command}, Output={Output}", command, output.Trim());
        }

        return exitCode == 0;
    }

    private static bool IsTunInterface(NetworkInterface networkInterface)
    {
        return IsTunInterface(networkInterface.Name, networkInterface.Description);
    }

    internal static bool IsSpecificRouteForDestination(RouteEntry route, string destinationIp, string tunIpAddress)
    {
        if (route.Network == "0.0.0.0" ||
            IsTunDefaultRoute(route, tunIpAddress) ||
            !IPAddress.TryParse(destinationIp, out var destination) ||
            !IPAddress.TryParse(route.Network, out var network) ||
            !IPAddress.TryParse(route.Netmask, out var netmask) ||
            destination.AddressFamily != AddressFamily.InterNetwork ||
            network.AddressFamily != AddressFamily.InterNetwork ||
            netmask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var destinationValue = ToUInt32(destination);
        var networkValue = ToUInt32(network);
        var maskValue = ToUInt32(netmask);
        return (destinationValue & maskValue) == (networkValue & maskValue);
    }

    internal static IPAddress? FindLocalAddressForDestination(
        IReadOnlyCollection<RouteEntry> routes,
        IPAddress destination,
        string tunIpAddress)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return routes
            .Where(route => IsRouteForDestination(route, destination, tunIpAddress))
            .OrderByDescending(route => GetPrefixLength(route.Netmask))
            .ThenBy(route => int.TryParse(route.Metric, out var metric) ? metric : int.MaxValue)
            .Select(route => TryParseBindableRouteInterface(route.Interface, tunIpAddress))
            .FirstOrDefault(address => address != null);
    }

    internal static int GetPrefixLength(string netmask)
    {
        if (!IPAddress.TryParse(netmask, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return 0;
        }

        var value = ToUInt32(address);
        var count = 0;
        while ((value & 0x80000000) != 0)
        {
            count++;
            value <<= 1;
        }

        return count;
    }

    private static bool IsUsablePhysicalInterface(NetworkInterface networkInterface)
    {
        return networkInterface.OperationalStatus == OperationalStatus.Up &&
               networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
               !IsTunInterface(networkInterface);
    }

    internal static string ResolveTunInterfaceName(
        IReadOnlyCollection<TunInterfaceCandidate> candidates,
        string tunIpAddress)
    {
        var tunCandidates = candidates
            .Where(candidate => IsTunInterface(candidate.Name, candidate.Description))
            .ToList();

        return tunCandidates
                   .FirstOrDefault(candidate =>
                       candidate.Name.Equals(PreferredTunInterfaceName, StringComparison.OrdinalIgnoreCase) &&
                       candidate.Ipv4Addresses.Contains(tunIpAddress, StringComparer.OrdinalIgnoreCase))
                   ?.Name
               ?? tunCandidates
                   .FirstOrDefault(candidate =>
                       candidate.Ipv4Addresses.Contains(tunIpAddress, StringComparer.OrdinalIgnoreCase))
                   ?.Name
               ?? tunCandidates
                   .FirstOrDefault(candidate =>
                       candidate.Name.Equals(PreferredTunInterfaceName, StringComparison.OrdinalIgnoreCase) &&
                       candidate.IsUp)
                   ?.Name
               ?? tunCandidates
                   .FirstOrDefault(candidate =>
                       candidate.Name.Equals(PreferredTunInterfaceName, StringComparison.OrdinalIgnoreCase))
                   ?.Name
               ?? tunCandidates.FirstOrDefault(candidate => candidate.IsUp)?.Name
               ?? tunCandidates.FirstOrDefault()?.Name
               ?? PreferredTunInterfaceName;
    }

    private static TunInterfaceCandidate CreateTunInterfaceCandidate(NetworkInterface networkInterface)
    {
        var ipv4Addresses = Array.Empty<string>();
        try
        {
            ipv4Addresses = networkInterface
                .GetIPProperties()
                .UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address.ToString())
                .ToArray();
        }
        catch
        {
        }

        return new TunInterfaceCandidate(
            networkInterface.Name,
            networkInterface.Description,
            networkInterface.OperationalStatus == OperationalStatus.Up,
            ipv4Addresses);
    }

    private static bool IsTunInterface(string name, string description)
    {
        return description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(PreferredTunInterfaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIPv4Address(string value)
    {
        return IPAddress.TryParse(value, out var address) &&
               address.AddressFamily == AddressFamily.InterNetwork;
    }

    private static bool IsRouteForDestination(RouteEntry route, IPAddress destination, string tunIpAddress)
    {
        if (IsTunDefaultRoute(route, tunIpAddress) ||
            !IPAddress.TryParse(route.Network, out var network) ||
            !IPAddress.TryParse(route.Netmask, out var netmask) ||
            network.AddressFamily != AddressFamily.InterNetwork ||
            netmask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var destinationValue = ToUInt32(destination);
        var networkValue = ToUInt32(network);
        var maskValue = ToUInt32(netmask);
        return (destinationValue & maskValue) == (networkValue & maskValue);
    }

    private static IPAddress? TryParseBindableRouteInterface(string value, string tunIpAddress)
    {
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString().Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase) ||
            IPAddress.IsLoopback(address) ||
            IPAddress.Any.Equals(address) ||
            IPAddress.None.Equals(address))
        {
            return null;
        }

        return address;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static bool IsAlreadyExistsOutput(string output)
    {
        return output.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("object already exists", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("已经存在", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("已存在", StringComparison.OrdinalIgnoreCase);
    }

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
            if (!proc.WaitForExit(5000))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup for a timed out helper process.
                }

                return (1, output + "Command timed out.");
            }

            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to execute command [{FileName} {Args}]: {Message}", fileName, arguments, ex.Message);
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
        Log.Information("=== Route diagnosis report ===");
        Log.Information(
            "TUN interface: {Status} (index {Index}, IP {IP})",
            TunInterfaceExists ? "present" : "missing",
            TunInterfaceIndex,
            TunIpAddress);
        Log.Information(
            "Default route: {Status} (metric={Metric}, competing={Competing})",
            HasDefaultRoute ? "present" : "missing",
            DefaultRouteMetric,
            CompetingRoutes);
        Log.Information("Internet connectivity: {Status}", InternetAccessible ? "yes" : "no");
        foreach (var issue in Issues)
        {
            Log.Warning("Route issue: {Issue}", issue);
        }

        if (Issues.Count == 0)
        {
            Log.Information("Route diagnosis: all checks passed.");
        }
    }
}

public sealed record TunInterfaceCandidate(
    string Name,
    string Description,
    bool IsUp,
    IReadOnlyCollection<string> Ipv4Addresses);
