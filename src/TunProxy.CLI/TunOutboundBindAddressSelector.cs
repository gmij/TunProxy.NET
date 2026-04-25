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
    private readonly Func<string, IPAddress[]> _resolveHost;
    private readonly Func<IPAddress, bool> _isUsableLocalAddress;

    public TunOutboundBindAddressSelector(
        Func<string, int, IPAddress?>? probeLocalIpForHost = null,
        Func<string, IPAddress?>? findLocalAddressForGateway = null,
        Func<string, IPAddress[]>? resolveHost = null,
        Func<IPAddress, bool>? isUsableLocalAddress = null)
    {
        _probeLocalIpForHost = probeLocalIpForHost ?? ProbeLocalIpForHost;
        _findLocalAddressForGateway = findLocalAddressForGateway ?? FindLocalAddressForGateway;
        _resolveHost = resolveHost ?? Dns.GetHostAddresses;
        _isUsableLocalAddress = isUsableLocalAddress ?? IsUsableLocalAddress;
    }

    public IPAddress? Select(
        ProxyConfig proxyConfig,
        IRouteService? routeService,
        IPAddress? preferredBindAddress = null)
    {
        if (IPAddress.TryParse(proxyConfig.Host, out var proxyAddress) &&
            IPAddress.IsLoopback(proxyAddress))
        {
            return null;
        }

        var proxyAddresses = ResolveProxyAddresses(proxyConfig.Host);
        var routed = SelectFromProxyRoute(proxyAddresses, routeService);
        if (TryUsePreferredAddress(preferredBindAddress, proxyAddresses, routeService, routed))
        {
            Log.Information(
                "[TUN ] Outbound bind address selected from runtime state: {BindAddress}",
                preferredBindAddress);
            return preferredBindAddress;
        }

        if (routed != null)
        {
            Log.Information(
                "[TUN ] Outbound bind address selected from proxy route: {BindAddress}",
                routed);
            return routed;
        }

        var probed = _probeLocalIpForHost(proxyConfig.Host, proxyConfig.Port);
        if (probed != null && !IPAddress.IsLoopback(probed))
        {
            return probed;
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
                return gatewayAddress;
            }
        }

        if (probed != null)
        {
            return null;
        }

        Log.Warning("[TUN ] Could not select a stable outbound bind address; proxy connections will use the OS route table.");
        return null;
    }

    private bool TryUsePreferredAddress(
        IPAddress? preferredBindAddress,
        IReadOnlyList<IPAddress> proxyAddresses,
        IRouteService? routeService,
        IPAddress? routedAddress)
    {
        if (preferredBindAddress == null ||
            preferredBindAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(preferredBindAddress) ||
            !_isUsableLocalAddress(preferredBindAddress))
        {
            return false;
        }

        if (routedAddress != null)
        {
            return preferredBindAddress.Equals(routedAddress);
        }

        foreach (var proxyAddress in proxyAddresses)
        {
            var routeAddress = routeService?.GetLocalAddressForDestination(proxyAddress);
            if (routeAddress != null)
            {
                return preferredBindAddress.Equals(routeAddress);
            }
        }

        return true;
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
