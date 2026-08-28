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
    private readonly object _routeStateLock = new();
    private readonly object _routeMutationLock = new();
    private readonly Dictionary<string, TrackedBypassRoute> _addedBypassRoutes = new(StringComparer.OrdinalIgnoreCase);
    private DirectEgressRoute? _directEgress;
    private bool _directEgressInitialized;

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
            Log.Warning(
                "[ROUTE] Removing stale default route via TUN gateway from non-TUN interface: 0.0.0.0 via {Gateway} on {Interface}",
                stale.Gateway,
                stale.Interface);
            ExecuteCommandWithOutput("route", $"delete 0.0.0.0 mask 0.0.0.0 {_tunIpAddress}");
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
        return GetDirectEgressRoute()?.Gateway;
    }

    public IPAddress? GetDirectOutboundAddress() => GetDirectEgressRoute()?.LocalAddress;

    public void RefreshRouteState()
    {
        var refreshed = SelectDirectEgressRoute(
            GetRouteTable(),
            GetDirectEgressInterfaceCandidates(),
            _tunIpAddress);
        DirectEgressRoute? previous;
        lock (_routeStateLock)
        {
            previous = _directEgress;
            _directEgress = refreshed;
            _directEgressInitialized = true;
        }

        if (previous == refreshed)
        {
            return;
        }

        if (refreshed == null)
        {
            Log.Warning("[ROUTE] No safe physical DIRECT egress is currently available.");
        }
        else
        {
            Log.Information(
                "[ROUTE] DIRECT egress changed: {Gateway} via {Interface} (index {InterfaceIndex}, local {LocalAddress}, metric {Metric}).",
                refreshed.Gateway,
                refreshed.InterfaceName,
                refreshed.InterfaceIndex,
                refreshed.LocalAddress,
                refreshed.TotalMetric);
        }

        RebuildTrackedBypassRoutes();
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
        lock (_routeMutationLock)
        {
            return AddBypassRouteCore(ipAddress, prefixLength, allowOverlayOnLink: false);
        }
    }

    public bool AddProxyBypassRoute(string ipAddress, int prefixLength = 32)
    {
        lock (_routeMutationLock)
        {
            return AddBypassRouteCore(ipAddress, prefixLength, allowOverlayOnLink: true);
        }
    }

    private bool AddBypassRouteCore(string ipAddress, int prefixLength, bool allowOverlayOnLink)
    {
        if (prefixLength == 32 &&
            IPAddress.TryParse(ipAddress, out var bypassAddress) &&
            bypassAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            var onLinkCandidate = FindOnLinkRouteCandidate(bypassAddress, allowOverlayOnLink);

            if (TryFindExistingSpecificRoute(ipAddress, out var existingRoute))
            {
                var existingRouteIsAllowed = allowOverlayOnLink || IsSafeDirectRoute(existingRoute);
                if (existingRouteIsAllowed &&
                    (onLinkCandidate == null || RouteUsesLocalAddress(existingRoute, onLinkCandidate.LocalAddress)))
                {
                    Log.Information(
                        "[ROUTE] Bypass route already covered by existing route: {IP}/{Prefix} via {Gateway} on {Interface}",
                        ipAddress,
                        prefixLength,
                        existingRoute.Gateway,
                        existingRoute.Interface);
                    return true;
                }

                Log.Information(
                    "[ROUTE] Existing bypass route for {IP}/{Prefix} uses unsafe or stale interface {ExistingInterface}; replacing it with {Interface}.",
                    ipAddress,
                    prefixLength,
                    existingRoute.Interface,
                    onLinkCandidate?.InterfaceName ?? "the current DIRECT egress");
                if (existingRoute.Network.Equals(ipAddress, StringComparison.OrdinalIgnoreCase) &&
                    GetPrefixLength(existingRoute.Netmask) == prefixLength)
                {
                    RemoveBypassRoute(ipAddress);
                }
            }

            if (onLinkCandidate != null)
            {
                return AddOnLinkBypassRoute(ipAddress, prefixLength, onLinkCandidate, allowOverlayOnLink);
            }
        }
        else if (prefixLength == 32 && TryFindExistingSpecificRoute(ipAddress, out var existingRoute))
        {
            Log.Information(
                "[ROUTE] Bypass route already covered by existing route: {IP}/{Prefix} via {Gateway} on {Interface}",
                ipAddress,
                prefixLength,
                existingRoute.Gateway,
                existingRoute.Interface);
            return true;
        }

        var egress = GetDirectEgressRoute();
        if (egress == null)
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

        var netshCommand =
            $"interface ipv4 add route {ipAddress}/{prefixLength} interface={egress.InterfaceIndex} nexthop={egress.Gateway} store=active";
        var (exitCode, output) = ExecuteCommandWithOutput("netsh", netshCommand);
        if ((exitCode == 0 || IsAlreadyExistsOutput(output)) &&
            RouteExistsOnInterface(ipAddress, mask, egress.LocalAddress))
        {
            Log.Information(
                "[ROUTE] Bypass route ready: {IP}/{Prefix} via {Gateway} on {Interface} (index {InterfaceIndex}).",
                ipAddress,
                prefixLength,
                egress.Gateway,
                egress.InterfaceName,
                egress.InterfaceIndex);
            _addedBypassRoutes[ipAddress] = new TrackedBypassRoute(prefixLength, allowOverlayOnLink);
            return true;
        }

        var routeCommand =
            $"add {ipAddress} mask {mask} {egress.Gateway} metric 5 IF {egress.InterfaceIndex}";
        var (routeExitCode, routeOutput) = ExecuteCommandWithOutput("route", routeCommand);
        if ((routeExitCode == 0 || IsAlreadyExistsOutput(routeOutput)) &&
            RouteExistsOnInterface(ipAddress, mask, egress.LocalAddress))
        {
            Log.Information(
                "[ROUTE] Bypass route ready: {IP}/{Prefix} via {Gateway} on interface index {InterfaceIndex}.",
                ipAddress,
                prefixLength,
                egress.Gateway,
                egress.InterfaceIndex);
            _addedBypassRoutes[ipAddress] = new TrackedBypassRoute(prefixLength, allowOverlayOnLink);
            return true;
        }

        // A command can succeed while Windows resolves an unreachable next hop back onto TUN.
        // Remove that route immediately instead of allowing a recursive connection storm.
        if (RouteExists(ipAddress, mask))
        {
            RemoveBypassRoute(ipAddress);
        }

        Log.Warning(
            "[ROUTE] Failed to add a verified bypass route {IP}/{Prefix} via {Gateway} on interface index {InterfaceIndex}. netsh={NetshOutput}; route={RouteOutput}",
            ipAddress,
            prefixLength,
            egress.Gateway,
            egress.InterfaceIndex,
            output.Trim(),
            routeOutput.Trim());
        return false;
    }

    private bool AddOnLinkBypassRoute(
        string ipAddress,
        int prefixLength,
        OnLinkRouteCandidate candidate,
        bool allowOverlayOnLink)
    {
        var mask = GetMaskForPrefixLength(prefixLength);
        var netshCommand = $"interface ipv4 add route {ipAddress}/{prefixLength} \"{candidate.InterfaceName}\" 0.0.0.0 store=active";
        var (exitCode, output) = ExecuteCommandWithOutput("netsh", netshCommand);
        if ((exitCode == 0 || IsAlreadyExistsOutput(output)) &&
            OnLinkRouteExists(ipAddress, mask, candidate.LocalAddress))
        {
            Log.Information(
                "[ROUTE] Bypass route ready: {IP}/{Prefix} on-link via {Interface} ({LocalAddress})",
                ipAddress,
                prefixLength,
                candidate.InterfaceName,
                candidate.LocalAddress);
            _addedBypassRoutes[ipAddress] = new TrackedBypassRoute(prefixLength, allowOverlayOnLink);
            return true;
        }

        var routeCommand = $"add {ipAddress} mask {mask} 0.0.0.0 IF {candidate.InterfaceIndex}";
        var (routeExitCode, routeOutput) = ExecuteCommandWithOutput("route", routeCommand);
        if ((routeExitCode == 0 || IsAlreadyExistsOutput(routeOutput)) &&
            OnLinkRouteExists(ipAddress, mask, candidate.LocalAddress))
        {
            Log.Information(
                "[ROUTE] Bypass route ready: {IP}/{Prefix} on-link via interface index {InterfaceIndex} ({LocalAddress})",
                ipAddress,
                prefixLength,
                candidate.InterfaceIndex,
                candidate.LocalAddress);
            _addedBypassRoutes[ipAddress] = new TrackedBypassRoute(prefixLength, allowOverlayOnLink);
            return true;
        }

        Log.Warning(
            "[ROUTE] Failed to add on-link bypass route {IP}/{Prefix} via {Interface} ({LocalAddress}). netsh={NetshOutput}; route={RouteOutput}",
            ipAddress,
            prefixLength,
            candidate.InterfaceName,
            candidate.LocalAddress,
            output.Trim(),
            routeOutput.Trim());
        return false;
    }

    private static string GetMaskForPrefixLength(int prefixLength) => prefixLength switch
    {
        24 => "255.255.255.0",
        16 => "255.255.0.0",
        _ => "255.255.255.255"
    };

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
        lock (_routeMutationLock)
        {
            if (!_addedBypassRoutes.Remove(ipAddress, out var trackedRoute))
            {
                return false;
            }

            if (RemoveBypassRoute(ipAddress))
            {
                return true;
            }

            _addedBypassRoutes[ipAddress] = trackedRoute;
            return false;
        }
    }

    public bool RemoveDefaultRoute()
    {
        var tunInterfaceName = GetTunInterfaceName();
        var removed = ExecuteNetshCommand($"interface ipv4 delete route 0.0.0.0/0 \"{tunInterfaceName}\" {_tunIpAddress}");
        var removedOnLink = ExecuteNetshCommand($"interface ipv4 delete route 0.0.0.0/0 \"{tunInterfaceName}\" 0.0.0.0");
        var removedByGateway = ExecuteCommandWithOutput("route", $"delete 0.0.0.0 mask 0.0.0.0 {_tunIpAddress}").ExitCode == 0;
        return removed || removedOnLink || removedByGateway;
    }

    public void ClearAllBypassRoutes()
    {
        lock (_routeMutationLock)
        {
            if (_addedBypassRoutes.Count == 0)
            {
                return;
            }

            Log.Information("[ROUTE] Removing {Count} bypass route(s).", _addedBypassRoutes.Count);
            foreach (var ip in _addedBypassRoutes.Keys.ToList())
            {
                RemoveBypassRoute(ip);
                Log.Debug("[ROUTE] Removed bypass route: {IP}", ip);
            }

            _addedBypassRoutes.Clear();
        }
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

        return route.Interface.Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase) &&
               (route.Gateway.Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase) ||
                IsOnLinkGateway(route.Gateway));
    }

    private bool TryAddDefaultRoute(string tunInterfaceName, string nextHop)
    {
        var command = $"interface ipv4 add route 0.0.0.0/0 \"{tunInterfaceName}\" {nextHop} metric=1 store=active";
        var (exitCode, output) = ExecuteCommandWithOutput("netsh", command);
        if (exitCode == 0 || IsAlreadyExistsOutput(output))
        {
            if (WaitForTunDefaultRouteReady())
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

    private bool WaitForTunDefaultRouteReady(int maxWaitMilliseconds = 1500)
    {
        var started = Stopwatch.StartNew();
        while (started.ElapsedMilliseconds < maxWaitMilliseconds)
        {
            if (HasTunDefaultRoute())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return HasTunDefaultRoute();
    }

    private DirectEgressRoute? GetDirectEgressRoute()
    {
        lock (_routeStateLock)
        {
            if (_directEgressInitialized)
            {
                return _directEgress;
            }
        }

        RefreshRouteState();
        lock (_routeStateLock)
        {
            return _directEgress;
        }
    }

    private OnLinkRouteCandidate? FindOnLinkRouteCandidate(IPAddress destination, bool allowOverlay)
    {
        try
        {
            return SelectBestOnLinkRouteCandidate(
                GetOnLinkRouteCandidates(NetworkInterface.GetAllNetworkInterfaces()),
                destination,
                _tunIpAddress,
                allowOverlay);
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to inspect on-link interfaces for {Destination}: {Message}", destination, ex.Message);
            return null;
        }
    }

    private static IEnumerable<OnLinkRouteCandidate> GetOnLinkRouteCandidates(IEnumerable<NetworkInterface> interfaces)
    {
        foreach (var networkInterface in interfaces)
        {
            if (!IsUsablePhysicalInterface(networkInterface))
            {
                continue;
            }

            IPInterfaceProperties properties;
            IPv4InterfaceProperties? ipv4Properties;
            try
            {
                properties = networkInterface.GetIPProperties();
                ipv4Properties = properties.GetIPv4Properties();
            }
            catch
            {
                continue;
            }

            if (ipv4Properties == null)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(unicast.Address) ||
                    IPAddress.Any.Equals(unicast.Address) ||
                    IPAddress.None.Equals(unicast.Address) ||
                    unicast.IPv4Mask == null)
                {
                    continue;
                }

                yield return new OnLinkRouteCandidate(
                    networkInterface.Name,
                    ipv4Properties.Index,
                    unicast.Address,
                    unicast.IPv4Mask,
                    IsOverlayInterface(networkInterface.Name, networkInterface.Description, networkInterface.NetworkInterfaceType));
            }
        }
    }

    internal static OnLinkRouteCandidate? SelectBestOnLinkRouteCandidate(
        IEnumerable<OnLinkRouteCandidate> candidates,
        IPAddress destination,
        string tunIpAddress,
        bool allowOverlay = true)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return candidates
            .Where(candidate =>
                candidate.LocalAddress.AddressFamily == AddressFamily.InterNetwork &&
                candidate.Netmask.AddressFamily == AddressFamily.InterNetwork &&
                !candidate.LocalAddress.ToString().Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase) &&
                (allowOverlay || !candidate.IsOverlay) &&
                IsAddressInSubnet(destination, candidate.LocalAddress, candidate.Netmask))
            .OrderByDescending(candidate => GetPrefixLength(candidate.Netmask.ToString()))
            .ThenBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsAddressInSubnet(IPAddress address, IPAddress localAddress, IPAddress netmask)
    {
        var addressValue = ToUInt32(address);
        var localValue = ToUInt32(localAddress);
        var maskValue = ToUInt32(netmask);
        return (addressValue & maskValue) == (localValue & maskValue);
    }

    private static bool RouteUsesLocalAddress(RouteEntry route, IPAddress localAddress)
    {
        return route.Interface.Equals(localAddress.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool RouteExists(string ipAddress, string mask = "255.255.255.255")
    {
        return GetRouteTable().Any(route => route.Network == ipAddress && route.Netmask == mask);
    }

    private bool OnLinkRouteExists(string ipAddress, string mask, IPAddress localAddress)
    {
        return GetRouteTable().Any(route =>
            route.Network == ipAddress &&
            route.Netmask == mask &&
            RouteUsesLocalAddress(route, localAddress));
    }

    private bool RouteExistsOnInterface(string ipAddress, string mask, IPAddress localAddress)
    {
        return GetRouteTable().Any(route =>
            route.Network.Equals(ipAddress, StringComparison.OrdinalIgnoreCase) &&
            route.Netmask.Equals(mask, StringComparison.OrdinalIgnoreCase) &&
            RouteUsesLocalAddress(route, localAddress) &&
            !route.Interface.Equals(_tunIpAddress, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSafeDirectRoute(RouteEntry route)
    {
        var egress = GetDirectEgressRoute();
        return egress != null && RouteUsesLocalAddress(route, egress.LocalAddress);
    }

    private void RebuildTrackedBypassRoutes()
    {
        lock (_routeMutationLock)
        {
            if (_addedBypassRoutes.Count == 0)
            {
                return;
            }

            var routes = _addedBypassRoutes.ToArray();
            Log.Information("[ROUTE] Rebuilding {Count} tracked bypass route(s) after DIRECT egress changed.", routes.Length);
            foreach (var (ipAddress, trackedRoute) in routes)
            {
                RemoveBypassRoute(ipAddress);
                if (!AddBypassRouteCore(ipAddress, trackedRoute.PrefixLength, trackedRoute.AllowOverlayOnLink))
                {
                    // Keep the desired route tracked so a later network update can retry it.
                    _addedBypassRoutes[ipAddress] = trackedRoute;
                }
            }
        }
    }

    private IReadOnlyList<DirectEgressInterfaceCandidate> GetDirectEgressInterfaceCandidates()
    {
        var candidates = new List<DirectEgressInterfaceCandidate>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsUsablePhysicalInterface(networkInterface))
                {
                    continue;
                }

                IPInterfaceProperties properties;
                IPv4InterfaceProperties? ipv4Properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                    ipv4Properties = properties.GetIPv4Properties();
                }
                catch
                {
                    continue;
                }

                if (ipv4Properties == null)
                {
                    continue;
                }

                var gateways = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .Where(address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.Any.Equals(address) &&
                        !IPAddress.None.Equals(address) &&
                        !address.ToString().Equals(_tunIpAddress, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var isOverlay = IsOverlayInterface(
                    networkInterface.Name,
                    networkInterface.Description,
                    networkInterface.NetworkInterfaceType);

                foreach (var unicast in properties.UnicastAddresses.Where(item =>
                             item.Address.AddressFamily == AddressFamily.InterNetwork &&
                             !IPAddress.IsLoopback(item.Address) &&
                             !item.Address.ToString().Equals(_tunIpAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(new DirectEgressInterfaceCandidate(
                        networkInterface.Name,
                        networkInterface.Description,
                        ipv4Properties.Index,
                        unicast.Address,
                        0,
                        isOverlay,
                        gateways));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[ROUTE] Failed to inspect DIRECT egress interfaces: {Message}", ex.Message);
        }

        return candidates;
    }

    internal static DirectEgressRoute? SelectDirectEgressRoute(
        IReadOnlyCollection<RouteEntry> routes,
        IReadOnlyCollection<DirectEgressInterfaceCandidate> interfaces,
        string tunIpAddress)
    {
        var eligibleInterfaces = interfaces
            .Where(candidate =>
                !candidate.IsOverlay &&
                !candidate.LocalAddress.ToString().Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var fromRouteTable = routes
            .Where(route =>
                route.Network == "0.0.0.0" &&
                route.Gateway != tunIpAddress &&
                !IsOnLinkGateway(route.Gateway) &&
                IsIPv4Address(route.Gateway))
            .SelectMany(route => eligibleInterfaces
                .Where(candidate => RouteUsesLocalAddress(route, candidate.LocalAddress))
                .Select(candidate => new DirectEgressRoute(
                    route.Gateway,
                    candidate.InterfaceName,
                    candidate.InterfaceIndex,
                    candidate.LocalAddress,
                    ParseMetric(route.Metric) + candidate.InterfaceMetric)))
            .OrderBy(candidate => candidate.TotalMetric)
            .ThenBy(candidate => candidate.InterfaceIndex)
            .FirstOrDefault();

        if (fromRouteTable != null)
        {
            return fromRouteTable;
        }

        return eligibleInterfaces
            .SelectMany(candidate => candidate.Gateways.Select(gateway => new DirectEgressRoute(
                gateway.ToString(),
                candidate.InterfaceName,
                candidate.InterfaceIndex,
                candidate.LocalAddress,
                candidate.InterfaceMetric)))
            .OrderBy(candidate => candidate.TotalMetric)
            .ThenBy(candidate => candidate.InterfaceIndex)
            .FirstOrDefault();
    }

    internal static bool IsOverlayInterface(
        string name,
        string description,
        NetworkInterfaceType interfaceType)
    {
        if (interfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        var identity = $"{name} {description}";
        string[] overlayMarkers =
        [
            "zerotier", "tailscale", "wireguard", "wintun", "openvpn", "tap-windows",
            "hamachi", "softether", "vpn", "virtual"
        ];
        return overlayMarkers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static int ParseMetric(string metric) =>
        int.TryParse(metric, out var value) ? value : int.MaxValue / 2;

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
            route.Interface.Equals(tunIpAddress, StringComparison.OrdinalIgnoreCase) ||
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

internal sealed record OnLinkRouteCandidate(
    string InterfaceName,
    int InterfaceIndex,
    IPAddress LocalAddress,
    IPAddress Netmask,
    bool IsOverlay = false);

internal sealed record DirectEgressInterfaceCandidate(
    string InterfaceName,
    string Description,
    int InterfaceIndex,
    IPAddress LocalAddress,
    int InterfaceMetric,
    bool IsOverlay,
    IReadOnlyCollection<IPAddress> Gateways);

internal sealed record DirectEgressRoute(
    string Gateway,
    string InterfaceName,
    int InterfaceIndex,
    IPAddress LocalAddress,
    int TotalMetric);

internal readonly record struct TrackedBypassRoute(int PrefixLength, bool AllowOverlayOnLink);

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
