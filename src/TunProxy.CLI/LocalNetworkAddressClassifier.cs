using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Serilog;
using TunProxy.Core.Packets;

namespace TunProxy.CLI;

internal static class LocalNetworkAddressClassifier
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly object CacheLock = new();
    private static IReadOnlyList<LocalNetworkSubnet> _cachedSubnets = [];
    private static DateTime _cacheExpiresUtc;

    public static bool IsLocalUseAddress(IPAddress address) =>
        ProtocolInspector.IsPrivateIp(address) ||
        IsInConfiguredLocalSubnet(address);

    internal static bool IsInConfiguredLocalSubnet(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return IsInConfiguredLocalSubnet(address, GetConfiguredLocalSubnets());
    }

    internal static LocalNetworkSubnet? FindConfiguredLocalSubnetForDestination(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        return GetConfiguredLocalSubnets()
            .Where(subnet => IsAddressInSubnet(destination, subnet.LocalAddress, subnet.Netmask))
            .OrderByDescending(static subnet => GetPrefixLength(subnet.Netmask))
            .ThenBy(static subnet => subnet.LocalAddress.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(static subnet => (LocalNetworkSubnet?)subnet)
            .FirstOrDefault();
    }

    internal static bool HasConfiguredLocalSubnet(LocalNetworkSubnet subnet) =>
        GetConfiguredLocalSubnets().Any(candidate =>
            candidate.LocalAddress.Equals(subnet.LocalAddress) &&
            candidate.Netmask.Equals(subnet.Netmask));

    internal static bool IsInConfiguredLocalSubnet(
        IPAddress address,
        IEnumerable<LocalNetworkSubnet> subnets)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return subnets.Any(subnet => IsAddressInSubnet(address, subnet.LocalAddress, subnet.Netmask));
    }

    private static IReadOnlyList<LocalNetworkSubnet> GetConfiguredLocalSubnets()
    {
        var now = DateTime.UtcNow;
        lock (CacheLock)
        {
            if (_cacheExpiresUtc > now)
            {
                return _cachedSubnets;
            }

            _cachedSubnets = DiscoverConfiguredLocalSubnets();
            _cacheExpiresUtc = now + CacheTtl;
            return _cachedSubnets;
        }
    }

    private static IReadOnlyList<LocalNetworkSubnet> DiscoverConfiguredLocalSubnets()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsUsableLocalInterface)
                .SelectMany(GetInterfaceSubnets)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug("[ROUTE] Failed to inspect local network subnets: {Message}", ex.Message);
            return [];
        }
    }

    private static IEnumerable<LocalNetworkSubnet> GetInterfaceSubnets(NetworkInterface networkInterface)
    {
        IPInterfaceProperties properties;
        try
        {
            properties = networkInterface.GetIPProperties();
        }
        catch
        {
            yield break;
        }

        foreach (var unicast in properties.UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                unicast.IPv4Mask == null ||
                IPAddress.IsLoopback(unicast.Address) ||
                IPAddress.Any.Equals(unicast.Address) ||
                IPAddress.None.Equals(unicast.Address))
            {
                continue;
            }

            yield return new LocalNetworkSubnet(unicast.Address, unicast.IPv4Mask);
        }
    }

    private static bool IsUsableLocalInterface(NetworkInterface networkInterface) =>
        networkInterface.OperationalStatus == OperationalStatus.Up &&
        networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
        !networkInterface.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) &&
        !networkInterface.Name.Contains("TunProxy", StringComparison.OrdinalIgnoreCase);

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

    private static int GetPrefixLength(IPAddress netmask)
    {
        var count = 0;
        foreach (var value in netmask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) == 0)
                {
                    return count;
                }

                count++;
            }
        }

        return count;
    }
}

internal readonly record struct LocalNetworkSubnet(IPAddress LocalAddress, IPAddress Netmask);
