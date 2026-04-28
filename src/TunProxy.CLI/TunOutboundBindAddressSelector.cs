using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal sealed class TunOutboundBindAddressSelector
{
    private readonly Func<string, int, IPAddress?> _probeLocalIpForHost;
    private readonly Func<string, IPAddress?> _findLocalAddressForGateway;
    private readonly Func<IPAddress, IPAddress?> _findLocalAddressForDestinationSubnet;
    private readonly Func<string, IPAddress[]> _resolveHost;
    private readonly Func<IPAddress, bool> _isUsableLocalAddress;

    public TunOutboundBindAddressSelector(
        Func<string, int, IPAddress?>? probeLocalIpForHost = null,
        Func<string, IPAddress?>? findLocalAddressForGateway = null,
        Func<IPAddress, IPAddress?>? findLocalAddressForDestinationSubnet = null,
        Func<string, IPAddress[]>? resolveHost = null,
        Func<IPAddress, bool>? isUsableLocalAddress = null)
    {
        _probeLocalIpForHost = probeLocalIpForHost ?? ProbeLocalIpForHost;
        _findLocalAddressForGateway = findLocalAddressForGateway ?? FindLocalAddressForGateway;
        _findLocalAddressForDestinationSubnet = findLocalAddressForDestinationSubnet ?? FindLocalAddressForDestinationSubnet;
        _resolveHost = resolveHost ?? Dns.GetHostAddresses;
        _isUsableLocalAddress = isUsableLocalAddress ?? IsUsableLocalAddress;
    }

    public IPAddress? Select(
        ProxyConfig proxyConfig,
        IRouteService? routeService,
        IPAddress? preferredBindAddress = null) =>
        SelectWithSource(proxyConfig, routeService, preferredBindAddress).Address;

    internal OutboundBindSelection SelectWithSource(
        ProxyConfig proxyConfig,
        IRouteService? routeService,
        IPAddress? preferredBindAddress = null)
    {
        if (IPAddress.TryParse(proxyConfig.Host, out var proxyAddress) &&
            IPAddress.IsLoopback(proxyAddress))
        {
            return new OutboundBindSelection(null, OutboundBindAddressSource.None);
        }

        var proxyAddresses = ResolveProxyAddresses(proxyConfig.Host);
        var subnetMatched = SelectFromLocalSubnet(proxyAddresses);
        var routed = SelectFromProxyRoute(proxyAddresses, routeService);
        if (TryUsePreferredAddress(preferredBindAddress, subnetMatched, routed))
        {
            Log.Information(
                "[TUN ] Outbound bind address selected from runtime state: {BindAddress}",
                preferredBindAddress);
            return new OutboundBindSelection(preferredBindAddress, OutboundBindAddressSource.RuntimeState);
        }

        if (subnetMatched != null)
        {
            Log.Information(
                "[TUN ] Outbound bind address selected from local subnet match: {BindAddress}",
                subnetMatched);
            return new OutboundBindSelection(subnetMatched, OutboundBindAddressSource.LocalSubnet);
        }

        if (routed != null)
        {
            Log.Information(
                "[TUN ] Outbound bind address selected from proxy route: {BindAddress}",
                routed);
            return new OutboundBindSelection(routed, OutboundBindAddressSource.ProxyRoute);
        }

        var probed = _probeLocalIpForHost(proxyConfig.Host, proxyConfig.Port);
        if (probed != null && !IPAddress.IsLoopback(probed))
        {
            return new OutboundBindSelection(probed, OutboundBindAddressSource.Probe);
        }

        var originalGateway = routeService?.GetOriginalDefaultGateway();
        if (!string.IsNullOrWhiteSpace(originalGateway))
        {
            var gatewayAddress = _findLocalAddressForGateway(originalGateway);
            if (gatewayAddress != null)
            {
                Log.Information(
                    "[TUN ] Outbound bind address selected from gateway {Gateway}: {BindAddress}",
                    originalGateway,
                    gatewayAddress);
                return new OutboundBindSelection(gatewayAddress, OutboundBindAddressSource.Gateway);
            }
        }

        if (probed != null)
        {
            return new OutboundBindSelection(null, OutboundBindAddressSource.None);
        }

        Log.Warning("[TUN ] Could not select a stable outbound bind address; proxy connections will use the OS route table.");
        return new OutboundBindSelection(null, OutboundBindAddressSource.None);
    }

    private bool TryUsePreferredAddress(
        IPAddress? preferredBindAddress,
        IPAddress? subnetMatchedAddress,
        IPAddress? routedAddress)
    {
        if (preferredBindAddress == null ||
            preferredBindAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(preferredBindAddress) ||
            !_isUsableLocalAddress(preferredBindAddress))
        {
            return false;
        }

        if (subnetMatchedAddress != null)
        {
            return preferredBindAddress.Equals(subnetMatchedAddress);
        }

        if (routedAddress != null)
        {
            return preferredBindAddress.Equals(routedAddress);
        }

        return true;
    }

    private IPAddress? SelectFromLocalSubnet(IReadOnlyList<IPAddress> proxyAddresses)
    {
        foreach (var address in proxyAddresses)
        {
            var localAddress = _findLocalAddressForDestinationSubnet(address);
            if (localAddress != null && !IPAddress.IsLoopback(localAddress))
            {
                return localAddress;
            }
        }

        return null;
    }

    private IPAddress? SelectFromProxyRoute(IReadOnlyList<IPAddress> proxyAddresses, IRouteService? routeService)
    {
        if (routeService == null)
        {
            return null;
        }

        foreach (var address in proxyAddresses)
        {
            var localAddress = routeService.GetLocalAddressForDestination(address);
            if (localAddress != null && !IPAddress.IsLoopback(localAddress))
            {
                return localAddress;
            }
        }

        return null;
    }

    private IReadOnlyList<IPAddress> ResolveProxyAddresses(string host)
    {
        if (IPAddress.TryParse(host, out var proxyIp))
        {
            return IsBindableProxyAddress(proxyIp) ? [proxyIp] : [];
        }

        try
        {
            return _resolveHost(host)
                .Where(IsBindableProxyAddress)
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsBindableProxyAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address);

    internal static bool IsUsableLocalAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up &&
                    networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !networkInterface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) &&
                    !networkInterface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase))
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
        }
        catch (Exception ex)
        {
            Log.Debug("[TUN ] Failed to validate cached outbound bind address: {Message}", ex.Message);
            return false;
        }
    }

    internal static IPAddress? FindLocalAddressForGateway(string gateway)
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    networkInterface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    networkInterface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var properties = networkInterface.GetIPProperties();
                if (!properties.GatewayAddresses.Any(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        address.Address.ToString().Equals(gateway, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var address = properties.UnicastAddresses
                    .Select(static unicast => unicast.Address)
                    .FirstOrDefault(static address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address));

                if (address != null)
                {
                    return address;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning("[TUN ] Failed to select outbound bind address for gateway {Gateway}: {Message}", gateway, ex.Message);
        }

        return null;
    }

    internal static IPAddress? FindLocalAddressForDestinationSubnet(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    networkInterface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    networkInterface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        unicast.IPv4Mask == null ||
                        IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    if (IsAddressInSubnet(destination, unicast.Address, unicast.IPv4Mask))
                    {
                        return unicast.Address;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[TUN ] Failed to match local subnet for {Destination}: {Message}", destination, ex.Message);
        }

        return null;
    }

    private static bool IsAddressInSubnet(IPAddress address, IPAddress localAddress, IPAddress netmask)
    {
        var addressBytes = address.GetAddressBytes();
        var localBytes = localAddress.GetAddressBytes();
        var maskBytes = netmask.GetAddressBytes();

        for (var i = 0; i < addressBytes.Length; i++)
        {
            if ((addressBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static IPAddress? ProbeLocalIpForHost(string host, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, port);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return null;
        }
    }
}

internal enum OutboundBindAddressSource
{
    None,
    RuntimeState,
    LocalSubnet,
    ProxyRoute,
    Probe,
    Gateway
}

internal readonly record struct OutboundBindSelection(
    IPAddress? Address,
    OutboundBindAddressSource Source)
{
    public bool IsReady =>
        Address != null &&
        Source is OutboundBindAddressSource.RuntimeState
            or OutboundBindAddressSource.LocalSubnet
            or OutboundBindAddressSource.ProxyRoute
            or OutboundBindAddressSource.Probe;
}
