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

    public TunOutboundBindAddressSelector(
        Func<string, int, IPAddress?>? probeLocalIpForHost = null,
        Func<string, IPAddress?>? findLocalAddressForGateway = null)
    {
        _probeLocalIpForHost = probeLocalIpForHost ?? ProbeLocalIpForHost;
        _findLocalAddressForGateway = findLocalAddressForGateway ?? FindLocalAddressForGateway;
    }

    public IPAddress? Select(ProxyConfig proxyConfig, IRouteService? routeService)
    {
        if (IPAddress.TryParse(proxyConfig.Host, out var proxyAddress) &&
            IPAddress.IsLoopback(proxyAddress))
        {
            return null;
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
