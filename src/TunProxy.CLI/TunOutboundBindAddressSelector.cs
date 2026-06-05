using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Packets;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal sealed class TunOutboundBindAddressSelector
{
    private readonly Func<string, int, IPAddress?> _probeLocalIpForHost;
    private readonly Func<string, IPAddress?> _findLocalAddressForGateway;
    private readonly Func<IPAddress, LocalNetworkSubnet?> _findLocalSubnetForDestination;
    private readonly Func<string, IPAddress[]> _resolveHost;
    private readonly Func<IPAddress, bool> _isUsableLocalAddress;
    private readonly Func<LocalNetworkSubnet, bool> _hasConfiguredLocalSubnet;

    public TunOutboundBindAddressSelector(
        Func<string, int, IPAddress?>? probeLocalIpForHost = null,
        Func<string, IPAddress?>? findLocalAddressForGateway = null,
        Func<IPAddress, LocalNetworkSubnet?>? findLocalSubnetForDestination = null,
        Func<string, IPAddress[]>? resolveHost = null,
        Func<IPAddress, bool>? isUsableLocalAddress = null,
        Func<LocalNetworkSubnet, bool>? hasConfiguredLocalSubnet = null)
    {
        _probeLocalIpForHost = probeLocalIpForHost ?? ProbeLocalIpForHost;
        _findLocalAddressForGateway = findLocalAddressForGateway ?? FindLocalAddressForGateway;
        _findLocalSubnetForDestination = findLocalSubnetForDestination ?? LocalNetworkAddressClassifier.FindConfiguredLocalSubnetForDestination;
        _resolveHost = resolveHost ?? Dns.GetHostAddresses;
        _isUsableLocalAddress = isUsableLocalAddress ?? IsUsableLocalAddress;
        _hasConfiguredLocalSubnet = hasConfiguredLocalSubnet ?? LocalNetworkAddressClassifier.HasConfiguredLocalSubnet;
    }

    public IPAddress? Select(
        ProxyConfig proxyConfig,
        IRouteService? routeService,
        IPAddress? preferredBindAddress = null) =>
        SelectWithSource(proxyConfig, routeService, preferredBindAddress).Address;

    internal OutboundBindSelection SelectWithSource(
        ProxyConfig proxyConfig,
        IRouteService? routeService,
        IPAddress? preferredBindAddress = null) =>
        SelectWithSource(
            proxyConfig,
            routeService,
            preferredBindAddress == null
                ? null
                : new TunOutboundBindState(preferredBindAddress, null, null, null, null));

    internal OutboundBindSelection SelectWithSource(
        ProxyConfig proxyConfig,
        IRouteService? routeService,
        TunOutboundBindState? preferredBindState)
    {
        if (IPAddress.TryParse(proxyConfig.Host, out var proxyAddress) &&
            IPAddress.IsLoopback(proxyAddress))
        {
            return new OutboundBindSelection(null, OutboundBindAddressSource.None);
        }

        var proxyAddresses = ResolveProxyAddresses(proxyConfig.Host);
        var requiresLocalSubnetConfirmation = proxyAddresses.Any(ProtocolInspector.IsPrivateIp);
        var subnetMatched = SelectFromLocalSubnet(proxyAddresses);
        var routed = SelectFromProxyRoute(proxyAddresses, routeService);
        if (TryUsePreferredAddress(preferredBindState, proxyAddresses, subnetMatched, routed))
        {
            var state = preferredBindState!.Value;
            Log.Information(
                "[TUN ] Outbound bind address selected from runtime state: {BindAddress}",
                state.Address);
            return new OutboundBindSelection(
                state.Address,
                OutboundBindAddressSource.RuntimeState,
                false,
                state.ProxyAddress,
                state.Netmask);
        }

        if (subnetMatched != null)
        {
            Log.Information(
                "[TUN ] Outbound bind address selected from local subnet match: {BindAddress}",
                subnetMatched.Value.LocalAddress);
            return new OutboundBindSelection(
                subnetMatched.Value.LocalAddress,
                OutboundBindAddressSource.LocalSubnet,
                false,
                subnetMatched.Value.Destination,
                subnetMatched.Value.Netmask);
        }

        if (routed != null)
        {
            Log.Information(
                requiresLocalSubnetConfirmation
                    ? "[TUN ] Outbound bind address selected from proxy route: {BindAddress}; waiting for local subnet confirmation because the proxy address is local-use."
                    : "[TUN ] Outbound bind address selected from proxy route: {BindAddress}",
                routed);
            return new OutboundBindSelection(
                routed,
                OutboundBindAddressSource.ProxyRoute,
                requiresLocalSubnetConfirmation,
                proxyAddresses.FirstOrDefault());
        }

        var probed = _probeLocalIpForHost(proxyConfig.Host, proxyConfig.Port);
        if (probed != null && !IPAddress.IsLoopback(probed))
        {
            return new OutboundBindSelection(
                probed,
                OutboundBindAddressSource.Probe,
                requiresLocalSubnetConfirmation,
                proxyAddresses.FirstOrDefault());
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
                return new OutboundBindSelection(
                    gatewayAddress,
                    OutboundBindAddressSource.Gateway,
                    requiresLocalSubnetConfirmation,
                    proxyAddresses.FirstOrDefault());
            }
        }

        if (probed != null)
        {
            return new OutboundBindSelection(
                null,
                OutboundBindAddressSource.None,
                requiresLocalSubnetConfirmation);
        }

        Log.Warning("[TUN ] Could not select a stable outbound bind address; proxy connections will use the OS route table.");
        return new OutboundBindSelection(
            null,
            OutboundBindAddressSource.None,
            requiresLocalSubnetConfirmation);
    }

    private bool TryUsePreferredAddress(
        TunOutboundBindState? preferredBindState,
        IReadOnlyList<IPAddress> proxyAddresses,
        LocalSubnetMatch? subnetMatchedAddress,
        IPAddress? routedAddress)
    {
        if (preferredBindState is not { } state)
        {
            return false;
        }

        var preferredBindAddress = state.Address;
        if (preferredBindAddress.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(preferredBindAddress) ||
            !_isUsableLocalAddress(preferredBindAddress))
        {
            return false;
        }

        if (TryUseRecordedLink(state, proxyAddresses))
        {
            return true;
        }

        if (subnetMatchedAddress != null)
        {
            return preferredBindAddress.Equals(subnetMatchedAddress.Value.LocalAddress);
        }

        if (routedAddress != null)
        {
            return preferredBindAddress.Equals(routedAddress);
        }

        return true;
    }

    private bool TryUseRecordedLink(TunOutboundBindState preferredBindState, IReadOnlyList<IPAddress> proxyAddresses)
    {
        if (preferredBindState.ProxyAddress == null ||
            preferredBindState.Netmask == null ||
            !proxyAddresses.Contains(preferredBindState.ProxyAddress))
        {
            return false;
        }

        var subnet = new LocalNetworkSubnet(preferredBindState.Address, preferredBindState.Netmask);
        if (!_hasConfiguredLocalSubnet(subnet))
        {
            return false;
        }

        return LocalNetworkAddressClassifier.IsInConfiguredLocalSubnet(
            preferredBindState.ProxyAddress,
            [subnet]);
    }

    private LocalSubnetMatch? SelectFromLocalSubnet(IReadOnlyList<IPAddress> proxyAddresses)
    {
        foreach (var address in proxyAddresses)
        {
            var subnet = _findLocalSubnetForDestination(address);
            if (subnet != null && !IPAddress.IsLoopback(subnet.Value.LocalAddress))
            {
                return new LocalSubnetMatch(address, subnet.Value.LocalAddress, subnet.Value.Netmask);
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
    OutboundBindAddressSource Source,
    bool RequiresLocalSubnetConfirmation = false,
    IPAddress? ProxyAddress = null,
    IPAddress? Netmask = null)
{
    public bool IsReady =>
        Address != null &&
        !RequiresLocalSubnetConfirmation &&
        Source is OutboundBindAddressSource.RuntimeState
            or OutboundBindAddressSource.LocalSubnet
            or OutboundBindAddressSource.ProxyRoute
            or OutboundBindAddressSource.Probe;
}

internal readonly record struct TunOutboundBindState(
    IPAddress Address,
    IPAddress? ProxyAddress,
    IPAddress? Netmask,
    OutboundBindAddressSource? Source,
    DateTime? SavedUtc);

internal readonly record struct LocalSubnetMatch(
    IPAddress Destination,
    IPAddress LocalAddress,
    IPAddress Netmask);
