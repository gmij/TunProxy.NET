using System.Net;
using System.Net.NetworkInformation;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Route;

namespace TunProxy.CLI;

internal sealed class TunMtuResolver
{
    internal const int DefaultTunMtu = 1500;
    private const int UpstreamOverheadReserve = 200;
    private const int MaxTunMtu = 9000;
    private const int MinRecommendedTunMtu = 1280;

    public int Resolve(ProxyConfig proxyConfig, IRouteService? routeService, IPAddress? preferredBindAddress)
    {
        var selection = new TunOutboundBindAddressSelector().SelectWithSource(
            proxyConfig,
            routeService,
            preferredBindAddress);
        if (selection.Address == null)
        {
            Log.Information(
                "[TUN ] Auto MTU fallback to default {Mtu}: no stable outbound bind address.",
                DefaultTunMtu);
            return DefaultTunMtu;
        }

        if (!TryGetUpstreamMtu(selection.Address, GetSystemInterfaceSnapshots(), out var upstreamMtu, out var interfaceName))
        {
            Log.Information(
                "[TUN ] Auto MTU fallback to default {Mtu}: no upstream interface matched bind address {BindAddress}.",
                DefaultTunMtu,
                selection.Address);
            return DefaultTunMtu;
        }

        var tunMtu = ComputeAutoTunMtu(upstreamMtu);
        Log.Information(
            "[TUN ] Auto MTU selected from upstream interface {Interface}: upstream={UpstreamMtu}, tun={TunMtu}.",
            interfaceName,
            upstreamMtu,
            tunMtu);
        return tunMtu;
    }

    internal static int ComputeAutoTunMtu(int upstreamMtu)
    {
        if (upstreamMtu <= 0)
        {
            return DefaultTunMtu;
        }

        var candidate = upstreamMtu - UpstreamOverheadReserve;
        if (candidate < DefaultTunMtu)
        {
            candidate = DefaultTunMtu;
        }

        candidate = Math.Min(candidate, MaxTunMtu);
        candidate = Math.Min(candidate, upstreamMtu);

        if (candidate < MinRecommendedTunMtu && upstreamMtu >= MinRecommendedTunMtu)
        {
            candidate = MinRecommendedTunMtu;
        }

        return candidate > 0 ? candidate : DefaultTunMtu;
    }

    internal static bool TryGetUpstreamMtu(
        IPAddress bindAddress,
        IEnumerable<InterfaceSnapshot> interfaces,
        out int mtu,
        out string interfaceName)
    {
        foreach (var snapshot in interfaces
                     .Where(static item =>
                         item.IsUp &&
                         !item.IsLoopback &&
                         !item.IsTunLike &&
                         item.Mtu > 0)
                     .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!snapshot.Addresses.Contains(bindAddress))
            {
                continue;
            }

            mtu = snapshot.Mtu;
            interfaceName = snapshot.Name;
            return true;
        }

        mtu = 0;
        interfaceName = string.Empty;
        return false;
    }

    internal static IEnumerable<InterfaceSnapshot> GetSystemInterfaceSnapshots()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            var name = networkInterface.Name;
            var description = networkInterface.Description;
            var isUp = networkInterface.OperationalStatus == OperationalStatus.Up;
            var isLoopback = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            var isTunLike = IsTunLike(name, description);

            var mtu = 0;
            try
            {
                mtu = networkInterface.GetIPProperties().GetIPv4Properties()?.Mtu ?? 0;
            }
            catch
            {
            }

            IPAddress[] addresses;
            try
            {
                addresses = networkInterface.GetIPProperties()
                    .UnicastAddresses
                    .Select(static item => item.Address)
                    .Where(static item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .ToArray();
            }
            catch
            {
                addresses = [];
            }

            yield return new InterfaceSnapshot(
                name,
                mtu,
                isUp,
                isLoopback,
                isTunLike,
                addresses);
        }
    }

    internal static bool IsTunLike(string? name, string? description)
    {
        return ContainsTunKeyword(name) || ContainsTunKeyword(description);
    }

    private static bool ContainsTunKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("tunproxy", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("utun", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record InterfaceSnapshot(
    string Name,
    int Mtu,
    bool IsUp,
    bool IsLoopback,
    bool IsTunLike,
    IPAddress[] Addresses);
